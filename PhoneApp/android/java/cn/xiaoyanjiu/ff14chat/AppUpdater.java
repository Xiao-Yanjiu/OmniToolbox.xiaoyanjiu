package cn.xiaoyanjiu.ff14chat;

import android.app.*;
import android.content.*;
import android.content.pm.*;
import android.net.Uri;
import android.os.*;
import android.widget.Toast;
import org.json.*;
import java.io.*;
import java.net.*;
import java.security.*;
import java.util.Arrays;
import java.util.concurrent.atomic.AtomicBoolean;

/** Releases are published by the repository owner; no GitHub credentials in the app. */
final class AppUpdater {
 static final String MANIFEST="https://raw.githubusercontent.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/PhoneApp/update.json";
 private final Activity activity;
 private final Handler ui=new Handler(Looper.getMainLooper());
 private final AtomicBoolean busy=new AtomicBoolean();
 private volatile boolean closed;
 private boolean pendingInstall;
 private ProgressDialog progress;
 AppUpdater(Activity a){activity=a;}
 private void post(Runnable action){ui.post(()->{if(!closed&&!activity.isFinishing()&&!activity.isDestroyed())action.run();});}
 private void message(String value){post(()->GameDialogStyle.builder(activity).setTitle("应用更新").setMessage(value).setPositiveButton("确定",null).show());}
 void close(){closed=true;if(progress!=null)progress.dismiss();}
 static boolean allowedUrl(String value,boolean asset) {
  try {URI u=new URI(value);if(!"https".equals(u.getScheme())||u.getPort()!=-1||u.getRawUserInfo()!=null||u.getRawFragment()!=null)return false;
   String host=u.getHost(),path=u.getPath();
   return asset?"github.com".equals(host)&&path.startsWith("/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/")&&path.endsWith(".apk"):
    "raw.githubusercontent.com".equals(host)&&path.equals("/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/main/PhoneApp/update.json");
  }catch(Exception e){return false;}
 }
 private static HttpURLConnection open(String value,boolean asset)throws Exception {
  if(!allowedUrl(value,asset))throw new IOException("更新地址无效");
  URL url=new URL(value);
  for(int i=0;i<6;i++){
   if(!"https".equals(url.getProtocol()))throw new IOException("更新连接必须使用 HTTPS");
   String host=url.getHost();if(!(host.equals("github.com")||host.equals("raw.githubusercontent.com")||host.equals("release-assets.githubusercontent.com")||host.equals("objects.githubusercontent.com")))throw new IOException("下载重定向地址无效");
   HttpURLConnection c=(HttpURLConnection)url.openConnection();c.setConnectTimeout(15000);c.setReadTimeout(30000);c.setInstanceFollowRedirects(false);c.setRequestProperty("User-Agent","Xiaoyanjiu-Android");c.setUseCaches(false);
   int code=c.getResponseCode();if(code==200)return c;
   String location=c.getHeaderField("Location");c.disconnect();if(code>=300&&code<400&&location!=null){url=new URL(url,location);continue;}
   throw new IOException("GitHub 返回 "+code);
  }throw new IOException("下载重定向过多");
 }
 void check(boolean manual){
  if(!busy.compareAndSet(false,true)){if(manual)message("正在检查或下载更新，请稍候");return;}
  if(manual)post(()->Toast.makeText(activity,"正在检查 GitHub 最新版本…",Toast.LENGTH_SHORT).show());
  new Thread(()->{try{
   HttpURLConnection c=open(MANIFEST,false);ByteArrayOutputStream data=new ByteArrayOutputStream();
   try(InputStream in=c.getInputStream()){byte[] buf=new byte[4096];int n;while((n=in.read(buf))!=-1){if(data.size()+n>65536)throw new IOException("更新信息过大");data.write(buf,0,n);}}finally{c.disconnect();}
   JSONObject info=new JSONObject(new String(data.toByteArray(),"UTF-8"));
   int code=info.getInt("versionCode");String version=info.getString("versionName"),url=info.getString("apkUrl"),hash=info.getString("sha256");long size=info.getLong("size");
   if(code<=0||version.length()>40||!allowedUrl(url,true)||!hash.matches("[a-fA-F0-9]{64}")||size<1||size>100*1024*1024)throw new IOException("更新信息无效");
   PackageInfo current=activity.getPackageManager().getPackageInfo(activity.getPackageName(),0);
   if(code<=current.versionCode){if(manual)message("当前已是最新版本 "+current.versionName);return;}
   String notes=info.optString("notes","");if(notes.length()>2000)notes=notes.substring(0,2000);final String text="当前版本："+current.versionName+"\n最新版本："+version+"\n下载大小："+String.format(java.util.Locale.ROOT,"%.1f MB",size/1048576.0)+"\n\n"+notes;
   post(()->GameDialogStyle.builder(activity).setTitle("发现新版本").setMessage(text).setNegativeButton("稍后",null).setPositiveButton("下载更新",(d,w)->download(url,hash,size,code)).show());
  }catch(Exception e){if(manual)message("检查更新失败，请检查网络后重试。\n"+e.getMessage());}finally{busy.set(false);}},"App-update-check").start();
 }
 private void download(String url,String hash,long size,int version){
  if(!busy.compareAndSet(false,true))return;
  progress=new ProgressDialog(activity);progress.setTitle("下载狒狒食柿更新");progress.setProgressStyle(ProgressDialog.STYLE_HORIZONTAL);progress.setMax(100);progress.setCancelable(false);progress.show();
  new Thread(()->{File part=new File(activity.getCacheDir(),"update.part"),apk=new File(activity.getCacheDir(),"update.apk");try{
   HttpURLConnection c=open(url,true);MessageDigest digest=MessageDigest.getInstance("SHA-256");long total=0;int last=-1;
   try(InputStream in=c.getInputStream();OutputStream out=new FileOutputStream(part)){byte[] buf=new byte[32768];int n;while((n=in.read(buf))!=-1){if(closed)throw new IOException("下载已取消");total+=n;if(total>size)throw new IOException("安装包大小不符");out.write(buf,0,n);digest.update(buf,0,n);int pct=(int)(total*100/size);if(pct!=last){last=pct;post(()->progress.setProgress(pct));}}}finally{c.disconnect();}
   StringBuilder actual=new StringBuilder();for(byte b:digest.digest())actual.append(String.format("%02x",b&255));
   if(total!=size||!actual.toString().equalsIgnoreCase(hash))throw new IOException("安装包校验失败，请重新下载");
   PackageManager pm=activity.getPackageManager();PackageInfo archive=pm.getPackageArchiveInfo(part.getAbsolutePath(),PackageManager.GET_SIGNATURES),installed=pm.getPackageInfo(activity.getPackageName(),PackageManager.GET_SIGNATURES);
   if(archive==null||!activity.getPackageName().equals(archive.packageName)||archive.versionCode!=version||archive.versionCode<=installed.versionCode||archive.signatures==null||!Arrays.equals(archive.signatures,installed.signatures))throw new IOException("安装包版本或签名不匹配");
   if(apk.exists()&&!apk.delete())throw new IOException("无法替换旧安装包");if(!part.renameTo(apk))throw new IOException("无法保存安装包");
   post(()->{progress.dismiss();install();});
  }catch(Exception e){part.delete();post(()->progress.dismiss());message("下载更新失败，请重试。\n"+e.getMessage());}finally{busy.set(false);}},"App-update-download").start();
 }
 private void install(){try{
  if(!activity.getPackageManager().canRequestPackageInstalls()){
   GameDialogStyle.builder(activity).setTitle("允许安装更新").setMessage("请在系统设置允许狒狒食柿安装应用，返回后继续安装。").setNegativeButton("稍后",null).setPositiveButton("前往设置",(d,w)->{try{pendingInstall=true;activity.startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+activity.getPackageName())));}catch(Exception e){pendingInstall=false;message("无法打开安装权限设置");}}).show();return;
  }
  Uri uri=Uri.parse("content://"+activity.getPackageName()+".updates/update.apk");Intent intent=new Intent(Intent.ACTION_VIEW).setDataAndType(uri,"application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);intent.setClipData(ClipData.newRawUri("狒狒食柿更新",uri));activity.startActivity(intent);
 }catch(Exception e){message("无法打开安装程序，请检查系统安装权限");}}
 void resume(){if(pendingInstall){pendingInstall=false;if(activity.getPackageManager().canRequestPackageInstalls())install();}}
}
