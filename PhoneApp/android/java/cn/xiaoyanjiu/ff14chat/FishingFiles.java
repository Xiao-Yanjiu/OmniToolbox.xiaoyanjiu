package cn.xiaoyanjiu.ff14chat;
import android.app.*;import android.content.*;import android.net.Uri;import android.webkit.*;import org.json.*;import java.io.*;
final class FishingFiles {
 static final int IMPORT=846,EXPORT=847,LIMIT=2*1024*1024;
 final FishingActivity host;ValueCallback<Uri[]> chooser;byte[] pending;int epoch;
 FishingFiles(FishingActivity a){host=a;}
 void toast(String s){android.widget.Toast.makeText(host,s,1).show();}
 boolean choose(ValueCallback<Uri[]> callback,WebChromeClient.FileChooserParams params){
  cancel();if(!FishingActivity.trusted(host.web.getUrl())){callback.onReceiveValue(null);return true;}
  chooser=callback;epoch=host.generation;
  try{Intent i=new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*");host.startActivityForResult(i,IMPORT);}catch(Exception e){cancel();toast("无法打开文件选择器");}return true;
 }
 void cancel(){if(chooser!=null){chooser.onReceiveValue(null);chooser=null;}pending=null;}
 void export(JSONObject d)throws Exception {
  if(pending!=null||chooser!=null)throw new Exception("请先完成当前文件操作");
  String data=d.getString("data");if(data.length()>LIMIT*4/3+8)throw new Exception("导出文件超过2MB");
  byte[] bytes=android.util.Base64.decode(data,android.util.Base64.DEFAULT);if(bytes.length>LIMIT)throw new Exception("导出文件超过2MB");
  String name=d.optString("name","鱼糕设置.json").replaceAll("[\\\\/\\p{Cntrl}]","_");if(name.length()>100)name=name.substring(0,100);if(name.isEmpty())name="鱼糕设置.json";
  epoch=host.generation;pending=bytes;
  try{host.startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/octet-stream").putExtra(Intent.EXTRA_TITLE,name),EXPORT);}catch(Exception e){pending=null;throw new Exception("无法打开文件保存窗口");}
 }
 void result(int request,int result,Intent data){
  if(request!=IMPORT&&request!=EXPORT)return;
  Uri uri=result==Activity.RESULT_OK&&data!=null?data.getData():null;
  if(epoch!=host.generation||!FishingActivity.trusted(host.web.getUrl())){cancel();return;}
  if(request==IMPORT){ValueCallback<Uri[]> cb=chooser;chooser=null;if(cb==null)return;if(uri==null||!"content".equals(uri.getScheme())){cb.onReceiveValue(null);return;}
   final int selectedEpoch=epoch;new Thread(()->{boolean ok=false;try(InputStream in=host.getContentResolver().openInputStream(uri)){if(in==null)throw new IOException();byte[] b=new byte[8192];int n,total=0;while((n=in.read(b))!=-1){total+=n;if(total>LIMIT)throw new IOException();}ok=true;}catch(Exception ignored){}final boolean valid=ok;host.runOnUiThread(()->{boolean current=selectedEpoch==host.generation&&FishingActivity.trusted(host.web.getUrl());cb.onReceiveValue(valid&&current?new Uri[]{uri}:null);if(!valid)toast("无法导入：文件不可读或超过2MB");});},"Fishing-import").start();
  }else{byte[] bytes=pending;pending=null;if(uri==null||bytes==null)return;
   new Thread(()->{String message;try(OutputStream out=host.getContentResolver().openOutputStream(uri,"w")){if(out==null)throw new IOException();out.write(bytes);message="鱼糕设置已导出";}catch(Exception e){message="文件保存失败，请重试";}final String done=message;host.runOnUiThread(()->toast(done));},"Fishing-export").start();
  }
 }
}
