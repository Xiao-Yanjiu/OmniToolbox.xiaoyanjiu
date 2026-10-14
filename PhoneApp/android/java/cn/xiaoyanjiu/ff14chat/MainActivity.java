package cn.xiaoyanjiu.ff14chat;

import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.webkit.*;
import android.widget.*;
import org.json.*;

public class MainActivity extends Activity {
 private WebView web;
 private volatile String pendingBoardView="",pendingListing="";
 private AppUpdater updater;
 private static final java.util.concurrent.atomic.AtomicBoolean updateChecked=new java.util.concurrent.atomic.AtomicBoolean();
 private FrameLayout root;
 private boolean pageReady;
 private JSONObject pendingPushOpen;
 private void receivePushIntent(Intent intent){if(intent==null)return;try{android.net.Uri uri=intent.getData();String channel="",target="";if(uri!=null){if(!"xiaoyanjiu".equals(uri.getScheme())||!"chat".equals(uri.getHost()))return;channel=uri.getQueryParameter("channel");target=uri.getQueryParameter("target");}else if(intent.hasExtra("channel")){channel=intent.getStringExtra("channel");target=intent.getStringExtra("target");}else return;if(channel==null||channel.length()>100||target!=null&&target.length()>100)return;pendingPushOpen=new JSONObject().put("channel",channel).put("target",target==null?"":target);deliverPushIntent();}catch(Exception ignored){}}
 private void deliverPushIntent(){if(!pageReady||pendingPushOpen==null)return;web.evaluateJavascript("openPushChat("+pendingPushOpen.toString()+")",null);pendingPushOpen=null;}
 @Override public void onNewIntent(Intent intent){super.onNewIntent(intent);setIntent(intent);receivePushIntent(intent);}
 private final Handler ui=new Handler(Looper.getMainLooper());
 private final java.util.concurrent.ExecutorService pairingExecutor=java.util.concurrent.Executors.newSingleThreadExecutor();
 private final java.util.concurrent.atomic.AtomicInteger pairingGeneration=new java.util.concurrent.atomic.AtomicInteger();
 private void manualConnect(String host,int port){final int generation=pairingGeneration.incrementAndGet();pairingExecutor.execute(()->{try{
   if(port<1||port>65535)throw new Exception("端口范围是1～65535");String value=host.trim();if(value.startsWith("https://"))value=value.substring(8);java.net.URI uri=new java.net.URI("https://"+value);if(uri.getHost()==null||uri.getRawUserInfo()!=null||uri.getPort()!=-1||!uri.getRawPath().isEmpty()||uri.getRawQuery()!=null||uri.getRawFragment()!=null)throw new Exception("请只填写IP地址或域名，端口单独填写");
   String address=new java.net.URI("https",null,uri.getHost(),port,null,null,null).toString(),pin=Transport.bootstrap(address);if(pairingGeneration.get()!=generation)return;
   android.content.SharedPreferences identity=getSharedPreferences("device",0);String id=identity.getString("id","");if(id.isEmpty()){id=java.util.UUID.randomUUID().toString();identity.edit().putString("id",id).apply();}byte[] random=new byte[32];new java.security.SecureRandom().nextBytes(random);StringBuilder secret=new StringBuilder();for(byte b:random)secret.append(String.format("%02x",b&255));JSONObject request=new JSONObject().put("id",id).put("secret",secret.toString()).put("name",Build.MANUFACTURER+" "+Build.MODEL).put("pin",pin);
   JSONObject reply=Transport.call(address,pin,"","/pair-request",request);String code=reply.getString("code");if(pairingGeneration.get()!=generation)return;pairProgress(code,"请在电脑模块输入此验证码并点击授权");long until=android.os.SystemClock.elapsedRealtime()+300000;
   while(pairingGeneration.get()==generation&&android.os.SystemClock.elapsedRealtime()<until){if(reply.optBoolean("approved")){if(pairingGeneration.get()!=generation)return;String credential=reply.getString("token");if(credential.length()!=64)throw new Exception("授权凭证无效");getSharedPreferences("pair",0).edit().putString("url",address).putString("pin",pin).putString("token",credential).apply();getSharedPreferences("receiver",0).edit().putBoolean("paused",false).apply();AccountStore.remember(this,null);ui.post(()->{if(pairingGeneration.get()!=generation)return;stopService(new Intent(this,ChatService.class));ChatService.initialize();startForegroundService(new Intent(this,ChatService.class));web.evaluateJavascript("manualPairDone()",null);Toast.makeText(this,"授权成功",Toast.LENGTH_SHORT).show();});return;}Thread.sleep(2000);reply=Transport.call(address,pin,"","/pair-status",request);}if(pairingGeneration.get()==generation)throw new Exception("授权请求已超时，请重新连接");
  }catch(Exception e){if(pairingGeneration.get()==generation)pairProgress("",e.getMessage()==null?"连接失败":e.getMessage());}});}
 private void pairProgress(String code,String text){ui.post(()->{if(!isFinishing()&&!isDestroyed())web.evaluateJavascript("manualPairProgress("+JSONObject.quote(code)+","+JSONObject.quote(text)+")",null);});}
 @Override public void onCreate(Bundle b){super.onCreate(b);updater=new AppUpdater(this);try{ChatService.initializeForAppStart(this);}catch(Exception e){ChatService.state="聊天缓存初始化失败："+e.getMessage();ui.post(()->showError(ChatService.state));}
  if(Build.VERSION.SDK_INT>=33)requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"},11);
  web=new WebView(this);web.setBackgroundColor(0xff15181f);web.getSettings().setJavaScriptEnabled(true);web.getSettings().setDomStorageEnabled(true);web.getSettings().setAllowFileAccess(false);web.getSettings().setAllowContentAccess(false);
  web.addJavascriptInterface(new Bridge(),"Phone");
  web.setWebViewClient(new WebViewClient(){@Override public boolean shouldOverrideUrlLoading(WebView w,WebResourceRequest r){return true;}
   @Override public void onPageFinished(WebView view,String url){if("file:///android_asset/index.html".equals(url)){pageReady=true;deliverPushIntent();if(updateChecked.compareAndSet(false,true))updater.check(false);}}
  });
  setContentView(web);
  getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
  root=new android.widget.FrameLayout(this);
  root.setBackgroundColor(0xff302b24);
  getWindow().setStatusBarColor(0xff302b24);getWindow().setNavigationBarColor(0xff302b24);
  getWindow().getDecorView().setSystemUiVisibility(0);
  ((android.view.ViewGroup)web.getParent()).removeView(web);
  root.addView(web,new android.widget.FrameLayout.LayoutParams(-1,-1));setContentView(root);
  if(Build.VERSION.SDK_INT>=30){
   getWindow().setDecorFitsSystemWindows(false);
   root.setOnApplyWindowInsetsListener((v,insets)->{
    android.graphics.Insets bars=insets.getInsets(android.view.WindowInsets.Type.systemBars());
    android.graphics.Insets ime=insets.getInsets(android.view.WindowInsets.Type.ime());
    root.setPadding(bars.left,bars.top,bars.right,Math.max(bars.bottom,ime.bottom));
    return android.view.WindowInsets.CONSUMED;
   });root.requestApplyInsets();
  }
  web.loadUrl("file:///android_asset/index.html");
  receivePushIntent(getIntent());
  applyTheme(getSharedPreferences("appearance",0).getString("settings","{}"));
  if(!getSharedPreferences("pair",0).getString("token","").isEmpty()&&!getSharedPreferences("receiver",0).getBoolean("paused",false))startForegroundService(new Intent(this,ChatService.class));
 }
 @Override public void onBackPressed(){if(web==null){super.onBackPressed();return;}web.evaluateJavascript("(function(){if(document.getElementById('recruitDialog')?.open){closeRecruitment();return true}if(document.getElementById('boardDialog')?.open){closeBoard();return true}const dialogs=[...document.querySelectorAll('dialog[open]')];if(dialogs.length){dialogs.at(-1).close();return true}return false})()",result->{if(!"true".equals(result))moveTaskToBack(true);});}
 @Override public void onResume(){super.onResume();if(updater!=null)updater.resume();ChatService.visible=true;if(!getSharedPreferences("pair",0).getString("token","").isEmpty()&&!getSharedPreferences("receiver",0).getBoolean("paused",false))startForegroundService(new Intent(this,ChatService.class).setAction("recover"));}
 @Override public void onPause(){ChatService.visible=false;super.onPause();}
 @Override public void onDestroy(){updater.close();pairingGeneration.incrementAndGet();pairingExecutor.shutdown();web.removeJavascriptInterface("Phone");web.destroy();super.onDestroy();}
 private void showError(String text){new AlertDialog.Builder(this).setTitle("连接提示").setMessage(text).setPositiveButton("知道了",null).show();}
 @Override public void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(result!=RESULT_OK||data==null)return;if(request==30&&data.getData()!=null)importBackground(data.getData());}

 private void applyTheme(String value){ui.post(()->{try{
  String theme=new JSONObject(value).optString("theme","black");boolean light=theme.equals("white")||theme.equals("yellow")||theme.equals("grey");
  int color=android.graphics.Color.parseColor(theme.equals("white")?"#f6f7fb":theme.equals("yellow")?"#f6e8bd":theme.equals("grey")?"#e2e5e9":theme.equals("game")?"#302b24":"#15181f");
  root.setBackgroundColor(color);getWindow().setStatusBarColor(color);getWindow().setNavigationBarColor(color);web.setBackgroundColor(color);
  if(Build.VERSION.SDK_INT>=30)getWindow().getInsetsController().setSystemBarsAppearance(light?android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS:0,android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);
  else getWindow().getDecorView().setSystemUiVisibility(light?android.view.View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|android.view.View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR:0);
 }catch(Exception ignored){}});}
 private java.io.File backgroundFile(){return new java.io.File(getFilesDir(),"chat-background.jpg");}
 private String readBackground(){try{java.io.File file=backgroundFile();if(!file.exists()||file.length()>3*1024*1024)return "";return "data:image/jpeg;base64,"+android.util.Base64.encodeToString(java.nio.file.Files.readAllBytes(file.toPath()),android.util.Base64.NO_WRAP);}catch(Exception e){return "";}}
 private void importBackground(android.net.Uri uri){new Thread(()->{android.graphics.Bitmap bitmap=null;java.io.FileOutputStream output=null;android.util.AtomicFile store=new android.util.AtomicFile(backgroundFile());try{
  android.graphics.BitmapFactory.Options bounds=new android.graphics.BitmapFactory.Options();bounds.inJustDecodeBounds=true;
  try(java.io.InputStream in=getContentResolver().openInputStream(uri)){android.graphics.BitmapFactory.decodeStream(in,null,bounds);}
  if(bounds.outWidth<=0||bounds.outHeight<=0)throw new Exception("无法读取这张图片，请选择 JPG 或 PNG");
  android.graphics.BitmapFactory.Options options=new android.graphics.BitmapFactory.Options();options.inSampleSize=1;while(Math.max(bounds.outWidth,bounds.outHeight)/options.inSampleSize>1600)options.inSampleSize*=2;
  try(java.io.InputStream in=getContentResolver().openInputStream(uri)){bitmap=android.graphics.BitmapFactory.decodeStream(in,null,options);}
  if(bitmap==null)throw new Exception("图片解码失败");
  int orientation=1;try(java.io.InputStream in=getContentResolver().openInputStream(uri)){orientation=new android.media.ExifInterface(in).getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION,1);}catch(Exception ignored){}
  if(orientation==3||orientation==6||orientation==8){android.graphics.Matrix matrix=new android.graphics.Matrix();matrix.postRotate(orientation==3?180:orientation==6?90:270);android.graphics.Bitmap rotated=android.graphics.Bitmap.createBitmap(bitmap,0,0,bitmap.getWidth(),bitmap.getHeight(),matrix,true);if(rotated!=bitmap){bitmap.recycle();bitmap=rotated;}}
  output=store.startWrite();if(!bitmap.compress(android.graphics.Bitmap.CompressFormat.JPEG,85,output))throw new Exception("保存背景失败");store.finishWrite(output);output=null;
  String image=readBackground();ui.post(()->web.evaluateJavascript("backgroundChanged("+JSONObject.quote(image)+")",null));
 }catch(Exception e){if(output!=null)store.failWrite(output);ui.post(()->showError(e.getMessage()));}finally{if(bitmap!=null)bitmap.recycle();}},"Chat-background").start();}
 private final class Bridge {
  @JavascriptInterface public String bitmapIcons(){return getSharedPreferences("display",0).getString("bitmapIcons","{}");}
  @JavascriptInterface public String statusIcons(){return getSharedPreferences("display",0).getString("statusIcons","{}");}
  @JavascriptInterface public String jobIcons(){return getSharedPreferences("display",0).getString("jobIcons","{}");}
  @JavascriptInterface public String symbolFont(){try{java.io.File font=new java.io.File(getFilesDir(),"game-symbol.ttf");return font.isFile()?android.util.Base64.encodeToString(java.nio.file.Files.readAllBytes(font.toPath()),android.util.Base64.NO_WRAP):"";}catch(Exception e){return "";}}
  @JavascriptInterface public void displayAssets(){ChatService.SEND.execute(()->{try{JSONObject assets=Transport.request(MainActivity.this,"/display-assets?kind=font",null);byte[] bytes=android.util.Base64.decode(assets.getString("symbolFont"),android.util.Base64.DEFAULT);if(bytes.length>2*1024*1024)throw new Exception("字体资源无效");android.util.AtomicFile file=new android.util.AtomicFile(new java.io.File(getFilesDir(),"game-symbol.ttf"));java.io.FileOutputStream out=file.startWrite();try{out.write(bytes);file.finishWrite(out);}catch(Exception e){file.failWrite(out);throw e;}for(String kind:new String[]{"jobs","status","bitmap"}){JSONObject data=Transport.request(MainActivity.this,"/display-assets?kind="+kind,null);String field=kind.equals("jobs")?"jobIcons":kind.equals("status")?"statusIcons":"bitmapIcons";if(data.optJSONObject(field)!=null)getSharedPreferences("display",0).edit().putString(field,data.getJSONObject(field).toString()).apply();}ui.post(()->web.evaluateJavascript("loadGameSymbols()",null));}catch(Exception ignored){}});}
  @JavascriptInterface public void boardListing(String view,String listing){pendingBoardView=view==null?"":view;pendingListing=listing==null?"":listing;}
  @JavascriptInterface public void recruitBoard(String action,String view,int index){ChatService.SEND.execute(()->{try{JSONObject result=Transport.request(MainActivity.this,"/recruit-board",new JSONObject().put("action",action).put("viewId",view).put("index",index));ui.post(()->web.evaluateJavascript("boardResult("+JSONObject.quote(action)+",true,"+result.toString()+")",null));}catch(Exception e){ui.post(()->web.evaluateJavascript("boardResult("+JSONObject.quote(action)+",false,{error:"+JSONObject.quote(e.getMessage())+"})",null));}});}
  @JavascriptInterface public void recruitment(String action,long messageId,String viewId){final String board=pendingBoardView,listing=pendingListing;ChatService.SEND.execute(()->{try{JSONObject result=Transport.request(MainActivity.this,"/recruit",new JSONObject().put("action",action).put("messageId",messageId).put("viewId",viewId).put("boardView",board).put("listingId",listing));ui.post(()->web.evaluateJavascript("recruitmentResult("+JSONObject.quote(action)+",true,"+result.toString()+")",null));}catch(Exception e){String error=JSONObject.quote(e.getMessage()==null?"操作失败":e.getMessage());ui.post(()->web.evaluateJavascript("recruitmentResult("+JSONObject.quote(action)+",false,{error:"+error+"})",null));}});}
  @JavascriptInterface public void checkUpdate(){ui.post(()->updater.check(true));}
  @JavascriptInterface public String appVersion(){try{return getPackageManager().getPackageInfo(getPackageName(),0).versionName;}catch(Exception e){return "";}}
  @JavascriptInterface public void history(String direction){ChatService.history(direction);}
  @JavascriptInterface public String snapshotFor(String current,String view,String thread){return ChatService.snapshotFor(current,view,thread);}
  @JavascriptInterface public void requestPair(String host,int port){manualConnect(host,port);}
  @JavascriptInterface public void cancelPair(){pairingGeneration.incrementAndGet();}
  @JavascriptInterface public String connectionAddress(){try{java.net.URI uri=new java.net.URI(getSharedPreferences("pair",0).getString("url","https://localhost:18765"));return new JSONObject().put("host",uri.getHost().equals("localhost")?"":uri.getHost()).put("port",uri.getPort()<0?18765:uri.getPort()).toString();}catch(Exception e){return "{}";}}
  @JavascriptInterface public String snapshotSelected(String current,String view,String thread,String selection){return ChatService.snapshotSelected(current,view,thread,selection);}
  @JavascriptInterface public String snapshot(){return ChatService.snapshot();}
  @JavascriptInterface public String loadNavigation(){return getSharedPreferences("navigation",0).getString("order","{}");}
  @JavascriptInterface public void saveNavigation(String value){if(value!=null&&value.length()<65536)getSharedPreferences("navigation",0).edit().putString("order",value).apply();}
  @JavascriptInterface public String loadAppearance(){return getSharedPreferences("appearance",0).getString("settings","{}");}
  @JavascriptInterface public void saveAppearance(String value){if(value==null||value.length()>4096)return;try{new JSONObject(value);getSharedPreferences("appearance",0).edit().putString("settings",value).apply();applyTheme(value);}catch(Exception ignored){}}
  @JavascriptInterface public String background(){return readBackground();}
  @JavascriptInterface public void chooseBackground(){ui.post(()->startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("image/*").addCategory(Intent.CATEGORY_OPENABLE),30));}
  @JavascriptInterface public void removeBackground(){backgroundFile().delete();ui.post(()->web.evaluateJavascript("backgroundChanged('')",null));}
  @JavascriptInterface public void reload(){ui.post(()->{if(getSharedPreferences("pair",0).getString("token","").isEmpty()){ChatService.state="未连接，请先连接设备";return;}getSharedPreferences("receiver",0).edit().putBoolean("paused",false).apply();ChatService.initialize();startForegroundService(new Intent(MainActivity.this,ChatService.class));});}
  @JavascriptInterface public void disconnect(){ui.post(()->{getSharedPreferences("receiver",0).edit().putBoolean("paused",true).apply();AccountStore.remember(MainActivity.this,ChatService.worldAccount);stopService(new Intent(MainActivity.this,ChatService.class));ChatService.state="已断开 · 授权已保留";});}
  @JavascriptInterface public String accounts(){return AccountStore.list(MainActivity.this);}
  @JavascriptInterface public void switchAccount(String id){ui.post(()->{try{stopService(new Intent(MainActivity.this,ChatService.class));AccountStore.select(MainActivity.this,id);ChatService.initialize();startForegroundService(new Intent(MainActivity.this,ChatService.class));web.evaluateJavascript("accountsChanged()",null);}catch(Exception e){showError(e.getMessage());}});}
  @JavascriptInterface public void reconnect(){ui.post(()->{if(getSharedPreferences("pair",0).getString("token","").isEmpty()){web.evaluateJavascript("openManualPair()",null);return;}getSharedPreferences("receiver",0).edit().putBoolean("paused",false).apply();startForegroundService(new Intent(MainActivity.this,ChatService.class).setAction("recover"));});}
  @JavascriptInterface public void forgetAccount(){ui.post(()->new AlertDialog.Builder(MainActivity.this).setTitle("重新授权当前连接").setMessage("清除当前连接凭据后，需要电脑重新批准授权。确认继续？").setNegativeButton("取消",null).setPositiveButton("继续",(dialog,which)->{getSharedPreferences("receiver",0).edit().putBoolean("paused",true).apply();stopService(new Intent(MainActivity.this,ChatService.class));getSharedPreferences("pair",0).edit().clear().apply();web.evaluateJavascript("openManualPair()",null);}).show());}
  @JavascriptInterface public void backgroundSettings(){ui.post(()->new AlertDialog.Builder(MainActivity.this).setTitle("通知与后台运行").setItems(new String[]{"通知权限","允许后台持续接收（忽略电池优化）","应用设置（后台运行 / 自启动）"},(dialog,which)->{try{Intent settings;if(which==0)settings=new Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE,getPackageName());else if(which==1&&!getSystemService(PowerManager.class).isIgnoringBatteryOptimizations(getPackageName()))settings=new Intent(android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,android.net.Uri.parse("package:"+getPackageName()));else settings=new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,android.net.Uri.parse("package:"+getPackageName()));startActivity(settings);}catch(Exception e){startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,android.net.Uri.parse("package:"+getPackageName())));}}).show());}
  @JavascriptInterface public void send(String channel,String target,String text){ChatService.SEND.execute(()->{try{Transport.request(MainActivity.this,"/send",new JSONObject().put("channel",channel).put("target",target).put("text",text));ui.post(()->web.evaluateJavascript("sent(true,'已提交到游戏')",null));}catch(Exception e){String message=JSONObject.quote(e.getMessage()==null?"发送失败":e.getMessage());ui.post(()->web.evaluateJavascript("sent(false,"+message+")",null));}});}
 }
}

