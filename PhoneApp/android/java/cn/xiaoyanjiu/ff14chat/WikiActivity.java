package cn.xiaoyanjiu.ff14chat;
import android.app.*;import android.os.*;import android.content.*;import android.net.Uri;import android.view.*;import android.webkit.*;import android.widget.*;import android.graphics.Color;import android.graphics.drawable.*;import android.content.res.ColorStateList;
public final class WikiActivity extends Activity {
 static final String HOME="https://ff14.huijiwiki.com/wiki/%E9%A6%96%E9%A1%B5";
 WebView web;TextView state;boolean failed;
 final Handler handler=new Handler(Looper.getMainLooper());final Runnable policy=new Runnable(){public void run(){if(!ChatService.pageEnabled("wiki")){finish();return;}handler.postDelayed(this,1500);}};
 static boolean allowed(String url){try{Uri u=Uri.parse(url);String h=u.getHost();return "https".equals(u.getScheme())&&h!=null&&(h.equals("huijiwiki.com")||h.endsWith(".huijiwiki.com"))&&(u.getPort()==-1||u.getPort()==443)&&u.getUserInfo()==null;}catch(Exception e){return false;}}
 int dp(int n){return Math.round(n*getResources().getDisplayMetrics().density);}
 void button(LinearLayout parent,String text,int width,Runnable action){TextView b=new TextView(this);b.setText(text);b.setTextColor(0xffece7db);b.setTextSize(12);b.setGravity(Gravity.CENTER);b.setContentDescription(text);b.setFocusable(true);GradientDrawable shape=new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,new int[]{0xff626262,0xff353535});shape.setCornerRadius(dp(16));shape.setStroke(dp(1),0xff757168);b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x44d8c99a),shape,null));b.setOnClickListener(v->action.run());LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(dp(width),dp(34));lp.setMargins(dp(2),0,dp(2),0);parent.addView(b,lp);}
 void external(String url){try{Uri u=Uri.parse(url==null?HOME:url);if(!"https".equals(u.getScheme()))u=Uri.parse(HOME);startActivity(new Intent(Intent.ACTION_VIEW,u));}catch(Exception e){state.setText("无法打开浏览器");}}
 @Override public void onCreate(Bundle saved){super.onCreate(saved);LinearLayout root=new LinearLayout(this);root.setOrientation(1);root.setBackgroundColor(0xff313031);
  LinearLayout bar=new LinearLayout(this);bar.setPadding(dp(8),dp(4),dp(8),dp(4));bar.setGravity(Gravity.CENTER_VERTICAL);button(bar,"‹ 返回",72,()->onBackPressed());TextView title=new TextView(this);title.setText("灰机Wiki");title.setTextColor(0xffeee6d0);title.setTextSize(15);title.setPadding(dp(8),0,0,0);bar.addView(title,new LinearLayout.LayoutParams(0,-2,1));button(bar,"刷新",56,()->web.reload());button(bar,"浏览器",72,()->external(web.getUrl()));root.addView(bar,new LinearLayout.LayoutParams(-1,dp(48)));
  state=new TextView(this);state.setTextSize(10);state.setTextColor(Color.LTGRAY);state.setPadding(dp(12),0,dp(12),dp(4));root.addView(state);
  web=new WebView(this);root.addView(web,new LinearLayout.LayoutParams(-1,0,1));
  getWindow().setStatusBarColor(0xff313031);getWindow().setNavigationBarColor(0xff313031);
  if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);root.setOnApplyWindowInsetsListener((v,i)->{android.graphics.Insets b=i.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()),ime=i.getInsets(WindowInsets.Type.ime());v.setPadding(b.left,b.top,b.right,Math.max(b.bottom,ime.bottom));return WindowInsets.CONSUMED;});}else root.setFitsSystemWindows(true);
  setContentView(root);root.requestApplyInsets();if(Build.VERSION.SDK_INT>=30){WindowInsetsController c=root.getWindowInsetsController();if(c!=null)c.setSystemBarsAppearance(0,WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);}
  WebSettings ws=web.getSettings();ws.setJavaScriptEnabled(true);ws.setDomStorageEnabled(true);ws.setAllowFileAccess(false);ws.setAllowContentAccess(false);ws.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);ws.setUseWideViewPort(true);ws.setLoadWithOverviewMode(true);ws.setTextZoom(100);
  web.setWebChromeClient(new WebChromeClient());web.setWebViewClient(new WebViewClient(){
   @Override public boolean shouldOverrideUrlLoading(WebView v,WebResourceRequest r){if(!r.isForMainFrame())return false;if(allowed(r.getUrl().toString()))return false;if(r.hasGesture()&&"https".equals(r.getUrl().getScheme()))external(r.getUrl().toString());return true;}
   @Override public void onPageStarted(WebView v,String url,android.graphics.Bitmap icon){failed=false;state.setText("正在加载灰机Wiki…");}
   @Override public void onPageFinished(WebView v,String url){if(!failed)state.setText("");}
   @Override public void onReceivedError(WebView v,WebResourceRequest r,WebResourceError e){if(r.isForMainFrame()){failed=true;state.setText("页面加载失败，请刷新或使用浏览器打开");}}
   @Override public void onReceivedHttpError(WebView v,WebResourceRequest r,WebResourceResponse e){if(r.isForMainFrame()){failed=true;state.setText("网站返回 "+e.getStatusCode()+"，可刷新或使用浏览器打开");}}
  });
  web.loadUrl(HOME);
 }
 @Override public void onResume(){super.onResume();handler.post(policy);}
 @Override public void onPause(){handler.removeCallbacks(policy);super.onPause();}
 @Override public void onBackPressed(){if(web!=null&&web.canGoBack())web.goBack();else finish();}
 @Override public void onDestroy(){handler.removeCallbacks(policy);if(web!=null)web.destroy();super.onDestroy();}
}
