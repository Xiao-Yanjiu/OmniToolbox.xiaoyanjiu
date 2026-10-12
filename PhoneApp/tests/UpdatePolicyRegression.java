package cn.xiaoyanjiu.ff14chat;

/** Run on a JDK with compiled Android classes and android.jar on the classpath. */
public final class UpdatePolicyRegression {
 private static void expect(String url,boolean asset,boolean expected){
  if(AppUpdater.allowedUrl(url,asset)!=expected)throw new AssertionError(url);
 }
 public static void main(String[] args){
  expect(AppUpdater.MANIFEST,false,true);
  String apk="https://github.com/Xiao-Yanjiu/OmniToolbox.xiaoyanjiu/releases/download/phone-v0.3.36/app.apk";
  expect(apk,true,true);
  expect(apk.replace("https:","http:"),true,false);
  expect(apk.replace("github.com","github.com.evil.example"),true,false);
  expect(apk.replace("github.com","evil@github.com"),true,false);
  expect(apk.replace("github.com","github.com:444"),true,false);
  expect(apk.replace("Xiao-Yanjiu","SomeoneElse"),true,false);
  expect(apk.replace(".apk",".zip"),true,false);
  expect(apk+"#fragment",true,false);
  expect(AppUpdater.MANIFEST.replace("main/","other/"),false,false);
  expect("https://localhost/update.apk",true,false);
  System.out.println("PASS: official repository only; rejects HTTP, foreign hosts/repos, userinfo, ports and non-APK assets");
 }
}
