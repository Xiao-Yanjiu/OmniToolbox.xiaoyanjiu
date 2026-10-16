package cn.xiaoyanjiu.ff14chat;
import android.view.*;import android.text.TextUtils;import android.graphics.drawable.*;import android.content.res.ColorStateList;
import android.app.*;import android.os.*;import android.webkit.*;import android.content.*;import android.content.pm.PackageManager;import android.net.Uri;import android.graphics.Color;import android.widget.*;import org.json.*;
/** Remote fishing page never receives the paired-game Phone interface. */
public final class FishingActivity extends Activity {
 static final String ORIGIN="https://fish.ffmomola.com";
 FishingFiles files;final Handler statusHandler=new Handler(Looper.getMainLooper());final Runnable statusTick=new Runnable(){public void run(){if(!ChatService.pageEnabled("fishing")){finish();return;}update();statusHandler.postDelayed(this,1500);}};
 WebView web;TextView status;WebMessagePort port;int generation;boolean ready;String error="";
 static boolean trusted(String value){try{Uri u=Uri.parse(value);return "https".equals(u.getScheme())&&"fish.ffmomola.com".equals(u.getHost())&&(u.getPort()==-1||u.getPort()==443)&&u.getUserInfo()==null;}catch(Exception e){return false;}}
 @Override public void onCreate(Bundle state){super.onCreate(state);files=new FishingFiles(this);
  LinearLayout root=new LinearLayout(this);root.setOrientation(1);root.setBackgroundColor(Color.rgb(49,48,49));LinearLayout bar=new LinearLayout(this);
  bar.setGravity(Gravity.CENTER_VERTICAL);bar.setPadding(dp(8),dp(4),dp(8),dp(4));
  button(bar,"‹ 返回",72,()->finish());TextView title=new TextView(this);title.setText("鱼糕");title.setTextSize(15);title.setTextColor(Color.rgb(238,230,208));title.setPadding(dp(10),0,0,0);bar.addView(title,new LinearLayout.LayoutParams(0,-2,1));
  button(bar,"刷新",56,()->web.reload());button(bar,"手机提醒",80,this::settings);root.addView(bar,new LinearLayout.LayoutParams(-1,dp(48)));
  getWindow().setStatusBarColor(Color.rgb(49,48,49));getWindow().setNavigationBarColor(Color.rgb(49,48,49));
  if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);root.setOnApplyWindowInsetsListener((v,insets)->{android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());android.graphics.Insets ime=insets.getInsets(WindowInsets.Type.ime());v.setPadding(bars.left,bars.top,bars.right,Math.max(bars.bottom,ime.bottom));return WindowInsets.CONSUMED;});}else root.setFitsSystemWindows(true);
  status=new TextView(this);status.setTextColor(Color.LTGRAY);status.setTextSize(10);status.setSingleLine(true);status.setEllipsize(TextUtils.TruncateAt.END);status.setPadding(dp(12),0,dp(12),dp(5));status.setOnClickListener(v->settings());root.addView(status);
  web=new WebView(this);root.addView(web,new LinearLayout.LayoutParams(-1,0,1));setContentView(root);root.requestApplyInsets();
  if(Build.VERSION.SDK_INT>=30){WindowInsetsController controller=root.getWindowInsetsController();if(controller!=null)controller.setSystemBarsAppearance(0,WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);}
  WebSettings ws=web.getSettings();ws.setTextZoom(100);ws.setUseWideViewPort(true);ws.setLoadWithOverviewMode(true);ws.setJavaScriptEnabled(true);ws.setDomStorageEnabled(true);ws.setAllowFileAccess(false);ws.setAllowContentAccess(true);ws.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);ws.setSupportMultipleWindows(false);
  web.setWebChromeClient(new WebChromeClient(){
   @Override public boolean onShowFileChooser(WebView view,ValueCallback<Uri[]> cb,FileChooserParams params){return files.choose(cb,params);}
   @Override public boolean onJsAlert(WebView view,String url,String message,JsResult r){if(!trusted(url)){r.cancel();return true;}new AlertDialog.Builder(FishingActivity.this).setMessage(message).setPositiveButton("确定",(d,n)->r.confirm()).setOnCancelListener(d->r.cancel()).show();return true;}
   @Override public boolean onJsConfirm(WebView view,String url,String message,JsResult r){if(!trusted(url)){r.cancel();return true;}new AlertDialog.Builder(FishingActivity.this).setMessage(message).setPositiveButton("确定",(d,n)->r.confirm()).setNegativeButton("取消",(d,n)->r.cancel()).setOnCancelListener(d->r.cancel()).show();return true;}
  });web.setWebViewClient(new WebViewClient(){
   @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest r){if(!r.isForMainFrame())return false;if(trusted(r.getUrl().toString()))return false;if(r.hasGesture()&&"https".equals(r.getUrl().getScheme()))try{startActivity(new Intent(Intent.ACTION_VIEW,r.getUrl()));}catch(Exception ignored){}return true;}
   @Override public void onPageStarted(WebView v,String u,android.graphics.Bitmap icon){generation++;ready=false;files.cancel();closePort();update();}
   @Override public void onPageFinished(WebView v,String u){if(trusted(u))connect();}
   @Override public void onReceivedError(WebView v,WebResourceRequest r,WebResourceError e){if(r.isForMainFrame()){error="鱼糕加载失败，请检查网络后刷新";update();}}
  });
  FishingReminders.channel(this);permission();web.loadUrl(ORIGIN+"/#/fishing");update();
 }
 int dp(int n){return Math.round(n*getResources().getDisplayMetrics().density);}
 void button(LinearLayout p,String label,int width,Runnable action){TextView b=new TextView(this);b.setText(label);b.setTextSize(12);b.setTextColor(Color.rgb(236,231,219));b.setGravity(Gravity.CENTER);b.setClickable(true);b.setFocusable(true);b.setContentDescription(label);GradientDrawable shape=new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,new int[]{0xff626262,0xff353535});shape.setCornerRadius(dp(16));shape.setStroke(dp(1),0xff757168);b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x44d8c99a),shape,null));b.setOnClickListener(v->action.run());LinearLayout.LayoutParams size=new LinearLayout.LayoutParams(dp(width),dp(34));size.setMargins(dp(2),0,dp(2),0);p.addView(b,size);}

 void closePort(){if(port!=null){port.close();port=null;}}
 void permission(){if(Build.VERSION.SDK_INT>=33&&checkSelfPermission("android.permission.POST_NOTIFICATIONS")!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{"android.permission.POST_NOTIFICATIONS"},842);else permissionResult();}
 void permissionResult(){if(port!=null)port.postMessage(new WebMessage("{\"action\":\"permissionResult\",\"permission\":\""+"granted"+"\"}"));update();}
 @Override public void onRequestPermissionsResult(int code,String[] names,int[] results){super.onRequestPermissionsResult(code,names,results);if(code==842)permissionResult();}
 void connect(){try{
  final int epoch=generation;StringBuilder source=new StringBuilder();for(String asset:new String[]{"fishing-layout.js","fishing-bridge.js"})try(java.io.InputStream in=getAssets().open(asset)){java.io.ByteArrayOutputStream bytes=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[8192];int count;while((count=in.read(buffer))!=-1)bytes.write(buffer,0,count);source.append(new String(bytes.toByteArray(),java.nio.charset.StandardCharsets.UTF_8)).append("\n");}String script=source.toString();script=script.replace("__NATIVE_PERMISSION__","granted");
  WebMessagePort[] ports=web.createWebMessageChannel();port=ports[0];
  port.setWebMessageCallback(new WebMessagePort.WebMessageCallback(){@Override public void onMessage(WebMessagePort p,WebMessage m){
   if(epoch!=generation||!trusted(web.getUrl()))return;try{String raw=m.getData();if(raw==null||raw.length()>2900000)throw new Exception("提醒数据过大");
    JSONObject d=new JSONObject(raw);String action=d.optString("action");
    if(action.equals("sync")){FishingReminders.sync(FishingActivity.this,d.getJSONArray("rows"));ready=true;error="";}
    else if(action.equals("test"))FishingServer.test(FishingActivity.this);
    else if(action.equals("notify")){FishingServer.notify(FishingActivity.this,d);}
    else if(action.equals("exportFile"))files.export(d);
    else if(action.equals("permission"))permission();
    else if(action.equals("error"))error="提醒同步失败，原已同步的提醒仍保留，请刷新重试";
    update();
   }catch(Exception e){error="提醒同步失败："+e.getMessage();update();}
  }});
  web.evaluateJavascript(script,result->{if(epoch==generation&&trusted(web.getUrl()))web.postWebMessage(new WebMessage("ff14-fishing-notifications",new WebMessagePort[]{ports[1]}),Uri.parse(ORIGIN));});
 }catch(Exception e){error="无法连接手机提醒："+e.getMessage();update();}}
 void update(){if(status!=null)status.setText(!error.isEmpty()?error:ready?FishingServer.summary(this).split("\\n")[0]:"正在连接鱼糕服务端提醒…");}
 void settings(){new AlertDialog.Builder(this).setTitle("鱼糕服务端通知").setItems(new String[]{FishingReminders.enabled(this)?"暂停服务端提醒":"开启服务端提醒","通知发送方式","App通知权限","清空并暂停提醒","查看状态与使用说明","发送服务端测试通知","立即重新同步"},(d,n)->{try{
  if(n==0)FishingReminders.setEnabled(this,!FishingReminders.enabled(this));
  else if(n==1)new AlertDialog.Builder(this).setTitle("通知发送方式").setSingleChoiceItems(new String[]{"自动：App在线直连，离线QQ／推送","仅App通知","QQ／推送服务（使用服务端配置）"},FishingServer.mode(this),(dialog,which)->{FishingServer.mode(this,which);dialog.dismiss();update();}).setNegativeButton("取消",null).show();
  else if(n==2)startActivity(new Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE,getPackageName()).putExtra(android.provider.Settings.EXTRA_CHANNEL_ID,FishingReminders.CHANNEL));
  else if(n==3){FishingReminders.setEnabled(this,false);FishingReminders.clear(this);}
  else if(n==4)new AlertDialog.Builder(this).setTitle("提醒状态").setMessage(FishingServer.summary(this)+"\n\n"+(FishingReminders.notifications(this)?"App通知权限已允许":"App通知权限未允许")+"\n在鱼糕中给目标鱼开启提醒。QQ和推送沿用游戏内服务端设置。服务端须保持运行；本地手机定时器已停用。").setPositiveButton("关闭",null).show();
  else if(n==5)FishingServer.test(this);else if(n==6)FishingServer.changed(this);
  update();
 }catch(Exception e){error="通知设置失败："+e.getMessage();update();}}).setNegativeButton("关闭",null).show();}
 @Override public void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);files.result(request,result,data);}
 @Override public void onResume(){super.onResume();FishingReminders.schedule(this);FishingServer.flush(this,true);permissionResult();statusHandler.removeCallbacks(statusTick);statusHandler.post(statusTick);}
 @Override public void onPause(){statusHandler.removeCallbacks(statusTick);super.onPause();}
 @Override public void onDestroy(){generation++;files.cancel();statusHandler.removeCallbacks(statusTick);closePort();if(web!=null)web.destroy();super.onDestroy();}
 @Override public void onBackPressed(){if(web.canGoBack())web.goBack();else finish();}
}
