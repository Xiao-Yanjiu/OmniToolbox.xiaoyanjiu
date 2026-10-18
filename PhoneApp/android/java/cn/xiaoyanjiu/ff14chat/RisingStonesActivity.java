package cn.xiaoyanjiu.ff14chat;
import android.app.*;import android.os.*;import android.content.*;import android.net.Uri;import android.view.*;import android.webkit.*;import android.widget.*;import android.graphics.Color;import java.util.ArrayList;
/** Isolated website windows share their cookie store and preserve window.opener for SSO. */
public final class RisingStonesActivity extends Activity {
 static final String HOME="https://ff14risingstones.web.sdo.com/pc/index.html#/post";
 WebView web;TextView state;FrameLayout root;boolean closing;final ArrayList<BrowserToolbar.Content> windows=new ArrayList<>();final java.util.WeakHashMap<WebView,String> failures=new java.util.WeakHashMap<>();
 final Handler handler=new Handler(Looper.getMainLooper());final Runnable policy=new Runnable(){public void run(){if(!ChatService.pageEnabled("risingStones")){finish();return;}handler.postDelayed(this,1500);}};
 static boolean allowed(String url){return RisingStonesNavigation.allowed(url);}
 WebView current(){return windows.isEmpty()?web:windows.get(windows.size()-1);}
 void external(String url){try{Uri u=Uri.parse(url==null?HOME:url);if(!allowed(u.toString()))u=Uri.parse(HOME);startActivity(new Intent(Intent.ACTION_VIEW,u));}catch(Exception e){state.setText("无法打开浏览器");}}
 void backToApp(){if(windows.size()>1)closeWindow(current());else finish();}
 void closeWindow(WebView target){if(closing)return;int index=windows.indexOf(target);if(index<1)return;CookieManager.getInstance().flush();while(windows.size()>index){WebView child=windows.remove(windows.size()-1);root.removeView(child);child.destroy();}BrowserToolbar.attach(this,root,windows.get(windows.size()-1));state.setText("");}
 boolean navigate(WebView view,String url){String secure=RisingStonesNavigation.upgradeHttp(url);if(secure!=null){state.setText("授权地址已升级为HTTPS，正在继续…");view.loadUrl(secure);return true;}if(allowed(url)||"about:blank".equals(url))return false;try{if(url.startsWith("intent:")){Intent intent=Intent.parseUri(url,Intent.URI_INTENT_SCHEME);String fallback=intent.getStringExtra("browser_fallback_url");if(allowed(fallback)){view.loadUrl(fallback);return true;}}}catch(Exception ignored){}String scheme=Uri.parse(url).getScheme();String reason="授权跳转被拦截（"+(scheme==null?"未知协议":scheme)+"），请返回石之家重新发起登录";failures.put(view,reason);state.setText(reason);return true;}
 BrowserToolbar.Content createWindow(){return configureWindow(new BrowserToolbar.Content(this));}
 BrowserToolbar.Content configureWindow(BrowserToolbar.Content view){WebSettings ws=view.getSettings();ws.setJavaScriptEnabled(true);ws.setDomStorageEnabled(true);ws.setAllowFileAccess(false);ws.setAllowContentAccess(false);ws.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);ws.setUseWideViewPort(true);ws.setLoadWithOverviewMode(true);ws.setTextZoom(100);ws.setSupportMultipleWindows(true);ws.setJavaScriptCanOpenWindowsAutomatically(true);CookieManager cookies=CookieManager.getInstance();cookies.setAcceptCookie(true);cookies.setAcceptThirdPartyCookies(view,true);
  view.setWebViewClient(new WebViewClient(){
   @Override public boolean shouldOverrideUrlLoading(WebView v,WebResourceRequest r){return r.isForMainFrame()&&navigate(v,r.getUrl().toString());}
   @Override public void onPageStarted(WebView v,String url,android.graphics.Bitmap icon){failures.remove(v);if(v==current())state.setText("正在加载石之家/登录页面…");}
   @Override public void onPageFinished(WebView v,String url){CookieManager.getInstance().flush();if(v==current())state.setText(failures.containsKey(v)?failures.get(v):"");}
   @Override public void onReceivedError(WebView v,WebResourceRequest r,WebResourceError e){if(v==current()&&r.isForMainFrame()){String reason="页面加载失败（"+e.getErrorCode()+"），请刷新重试";failures.put(v,reason);state.setText(reason);}}
   @Override public void onReceivedHttpError(WebView v,WebResourceRequest r,WebResourceResponse e){if(v==current()&&r.isForMainFrame()){String reason="网站返回 "+e.getStatusCode()+"，请刷新重试";failures.put(v,reason);state.setText(reason);}}
  });
  view.setWebChromeClient(new WebChromeClient(){
   @Override public boolean onJsAlert(WebView v,String url,String message,JsResult result){if(closing){result.cancel();return true;}GameDialogStyle.builder(RisingStonesActivity.this).setTitle("网页提示").setMessage(message).setPositiveButton("确定",(d,n)->result.confirm()).setOnCancelListener(d->result.cancel()).show();return true;}
   @Override public boolean onJsConfirm(WebView v,String url,String message,JsResult result){if(closing){result.cancel();return true;}GameDialogStyle.builder(RisingStonesActivity.this).setTitle("网页确认").setMessage(message).setPositiveButton("确定",(d,n)->result.confirm()).setNegativeButton("取消",(d,n)->result.cancel()).setOnCancelListener(d->result.cancel()).show();return true;}
   @Override public boolean onJsPrompt(WebView v,String url,String message,String value,JsPromptResult result){if(closing){result.cancel();return true;}EditText input=new EditText(RisingStonesActivity.this);input.setText(value);GameDialogStyle.builder(RisingStonesActivity.this).setTitle("网页输入").setMessage(message).setView(input).setPositiveButton("确定",(d,n)->result.confirm(input.getText().toString())).setNegativeButton("取消",(d,n)->result.cancel()).setOnCancelListener(d->result.cancel()).show();return true;}
   @Override public boolean onConsoleMessage(ConsoleMessage message){if(view==current()){if(message.messageLevel()==ConsoleMessage.MessageLevel.ERROR){String reason="授权页面脚本异常（第 "+message.lineNumber()+" 行），可刷新后重试";failures.put(view,reason);state.setText(reason);}else if(message.message().startsWith("nav_ur=")){state.setText("正在提交授权…");}}return true;}

   @Override public boolean onCreateWindow(WebView opener,boolean dialog,boolean gesture,Message result){if(closing||windows.size()>=4){state.setText("登录窗口过多，请返回后重试");return false;}BrowserToolbar.Content child=createWindow();windows.add(child);BrowserToolbar.attach(RisingStonesActivity.this,root,child);WebView.WebViewTransport transport=(WebView.WebViewTransport)result.obj;transport.setWebView(child);result.sendToTarget();return true;}
   @Override public void onCloseWindow(WebView window){closeWindow(window);}
  });return view;
 }
 final java.util.concurrent.ExecutorService glamourExecutor=java.util.concurrent.Executors.newSingleThreadExecutor();
 void importGlamour(){
  final WebView source=current();final String url=source.getUrl();
  try{if(!RisingStonesGlamour.allowedSource(url))throw new Exception("请先打开石之家幻化详情页");if(!ChatService.pageEnabled("glamour"))throw new Exception("请先在服务端页面管理启用Omni幻化管理");}
  catch(Exception e){state.setText(e.getMessage());return;}
  state.setText("正在读取当前幻化…");
  try{String script;try(java.io.InputStream input=getAssets().open("rising-glamour-export.js")){java.io.ByteArrayOutputStream out=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[4096];int n;while((n=input.read(buffer))!=-1)out.write(buffer,0,n);script=out.toString("UTF-8");}source.evaluateJavascript(script,null);pollGlamourExport(source,url,0);}catch(Exception e){state.setText("导入失败："+e.getMessage());}
 }
 void pollGlamourExport(WebView source,String url,int attempt){
  if(closing||source!=current()||!url.equals(source.getUrl()))return;
  source.evaluateJavascript("window.__omniGlamourExport||null",value->{try{org.json.JSONObject result=new org.json.JSONObject(value);if(result.optBoolean("pending")){if(attempt>=100)throw new Exception("读取幻化超时，请刷新后重试");handler.postDelayed(()->pollGlamourExport(source,url,attempt+1),150);return;}if(result.has("error"))throw new Exception(result.optString("error"));submitGlamourExport(source,url,result.getJSONObject("data").toString());}catch(Exception e){state.setText("导入失败："+e.getMessage());}});
 }
 void submitGlamourExport(WebView source,String url,String value){
  try{if(source!=current()||!url.equals(source.getUrl()))throw new Exception("页面已变化，请重试");if(value==null||value.length()>65536||value.equals("null"))throw new Exception("请等待装备列表加载完成");org.json.JSONObject data=new org.json.JSONObject(value);if(data.getJSONArray("parts").length()<12)throw new Exception("装备列表未加载完整");glamourExecutor.execute(()->{try{org.json.JSONObject result=Transport.request(this,"/omni-glamour",new org.json.JSONObject().put("action","import-website").put("data",data));if(!result.optBoolean("ok"))throw new Exception(result.optString("error","导入失败"));handler.post(()->{if(closing)return;state.setText("已添加到Omni幻化管理；未导入网页发型和时尚配饰");GameDialogStyle.builder(this).setTitle("幻化已添加").setMessage("已生成新的幻化条目，发型沿用当前角色；时尚配饰暂不支持。是否查看？").setPositiveButton("查看",(d,w)->{startActivity(new Intent(this,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra("navigatePage","glamour"));finish();}).setNegativeButton("继续浏览",null).show();});}catch(Exception e){handler.post(()->{if(!closing)state.setText("导入失败："+e.getMessage());});}});}
  catch(Exception e){state.setText(e.getMessage());}
 }
 @Override public void onCreate(Bundle saved){super.onCreate(saved);root=new FrameLayout(this);root.setBackgroundColor(0xff313031);root.addView(BrowserToolbar.create(this,"risingStones","石之家",this::backToApp,()->current().reload(),"浏览器",()->external(current().getUrl()),"导入幻化",this::importGlamour),new FrameLayout.LayoutParams(-1,-1));state=BrowserToolbar.hintSink(this);java.util.ArrayList<BrowserToolbar.Content> retained=BrowserPages.take(this,"risingStones");boolean restored=!retained.isEmpty();if(restored){for(BrowserToolbar.Content item:retained)windows.add(configureWindow(item));}else windows.add(createWindow());web=windows.get(0);BrowserToolbar.attach(this,root,(BrowserToolbar.Content)current());BrowserToolbar.home(this,()->{while(windows.size()>1)closeWindow(current());web.loadUrl(HOME);});
  getWindow().setStatusBarColor(0xff313031);getWindow().setNavigationBarColor(0xff313031);
  if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);root.setOnApplyWindowInsetsListener((v,i)->{android.graphics.Insets b=i.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()),ime=i.getInsets(WindowInsets.Type.ime());v.setPadding(b.left,b.top,b.right,Math.max(b.bottom,ime.bottom));return WindowInsets.CONSUMED;});}else root.setFitsSystemWindows(true);
  setContentView(root);root.requestApplyInsets();if(Build.VERSION.SDK_INT>=30){WindowInsetsController c=root.getWindowInsetsController();if(c!=null)c.setSystemBarsAppearance(0,WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);}if(!restored)web.loadUrl(BrowserPages.startUrl(this,"risingStones",HOME));
 }
 @Override public void onResume(){super.onResume();handler.post(policy);}
 @Override public void onPause(){handler.removeCallbacks(policy);CookieManager.getInstance().flush();super.onPause();}
 @Override public void onBackPressed(){WebView active=current();if(active!=null&&active.canGoBack())active.goBack();else backToApp();}
 @Override public void onDestroy(){closing=true;glamourExecutor.shutdownNow();handler.removeCallbacks(policy);CookieManager.getInstance().flush();BrowserDrawer.close(this);BrowserToolbar.close(this);BrowserPages.park(this,"risingStones",windows);windows.clear();super.onDestroy();}
}
