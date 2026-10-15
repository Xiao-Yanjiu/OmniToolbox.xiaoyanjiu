package cn.xiaoyanjiu.ff14chat;

import android.content.Context;
import org.json.JSONObject;
import javax.net.ssl.*;
import java.io.*;
import java.net.*;
import java.security.MessageDigest;
import java.security.cert.X509Certificate;

final class Transport {
 static String bootstrap(String address)throws Exception{
  URL url=new URL(address+"/pair-info");if(!url.getProtocol().equals("https"))throw new IOException("必须使用HTTPS");final String[] pin={""};
  TrustManager[] trust={new X509TrustManager(){public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}public void checkClientTrusted(X509Certificate[] c,String a)throws java.security.cert.CertificateException{throw new java.security.cert.CertificateException();}public void checkServerTrusted(X509Certificate[] chain,String auth)throws java.security.cert.CertificateException{try{chain[0].checkValidity();StringBuilder value=new StringBuilder();for(byte b:MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()))value.append(String.format("%02x",b&255));pin[0]=value.toString();}catch(Exception e){throw new java.security.cert.CertificateException(e);}}}};
  SSLContext ssl=SSLContext.getInstance("TLS");ssl.init(null,trust,null);HttpsURLConnection conn=(HttpsURLConnection)url.openConnection(Proxy.NO_PROXY);conn.setSSLSocketFactory(ssl.getSocketFactory());conn.setHostnameVerifier((host,session)->host.equals(url.getHost()));conn.setInstanceFollowRedirects(false);conn.setConnectTimeout(12000);conn.setReadTimeout(12000);
  try{if(conn.getResponseCode()!=200)throw new IOException("电脑模块不支持手动授权，请更新模块");try(InputStream stream=conn.getInputStream()){byte[] buffer=new byte[2048];int total=0,n;while((n=stream.read(buffer))!=-1){total+=n;if(total>8192)throw new IOException("授权响应无效");}}if(pin[0].length()!=64)throw new IOException("未取得服务器证书");return pin[0];}finally{conn.disconnect();}
 }
 private static volatile HttpsURLConnection pollConnection;
 private static final java.util.concurrent.atomic.AtomicInteger pollGeneration=new java.util.concurrent.atomic.AtomicInteger();
 static void cancelPoll(){pollGeneration.incrementAndGet();HttpsURLConnection connection=pollConnection;if(connection!=null)connection.disconnect();}
 static JSONObject request(Context context,String path,JSONObject body) throws Exception {
  android.content.SharedPreferences p=context.getSharedPreferences("pair",0);
  String expected=p.getString("expectedCharacterKey","");if(!expected.isEmpty()&&!path.startsWith("/poll")&&!path.equals("/reload")&&!path.equals("/pair-info")&&!path.equals("/display-assets")&&!path.equals("/icons")){JSONObject game=ChatService.gameAccount;if(game==null||!expected.equals(game.optString("name")+"@"+game.optString("world")))throw new IOException("电脑当前角色与所选账号不同，请在电脑切换角色后重试");}
  return call(p.getString("url",""),p.getString("pin",""),p.getString("token",""),path,body);
 }
 static JSONObject call(String address,String pin,String token,String path,JSONObject body) throws Exception {
  int generation=pollGeneration.get();
  HttpsURLConnection conn=openPinned(address,pin,token,path);conn.setReadTimeout(path.equals("/stream")&&body!=null&&body.optString("action").equals("start")?200000:path.startsWith("/poll")?14000:15000);
  if(path.equals("/stream-input")||path.equals("/stream-pointer")||path.equals("/stream-control")||path.equals("/stream-text")){conn.setConnectTimeout(1000);conn.setReadTimeout(1500);}
  if(path.equals("/stream")&&body!=null&&body.optString("action").equals("status")){conn.setConnectTimeout(1500);conn.setReadTimeout(3000);}
  if(path.startsWith("/poll"))pollConnection=conn;
  try{
   if(body!=null){byte[] bytes=body.toString().getBytes("UTF-8");conn.setRequestMethod("POST");conn.setDoOutput(true);conn.setRequestProperty("Content-Type","application/json");conn.setFixedLengthStreamingMode(bytes.length);try(OutputStream out=conn.getOutputStream()){out.write(bytes);}}
   int code=conn.getResponseCode();InputStream stream=code>=400?conn.getErrorStream():conn.getInputStream();
   if(stream==null)throw new IOException("服务器返回 "+code);
   ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int n;
   try(InputStream in=stream){while((n=in.read(buffer))!=-1){bytes.write(buffer,0,n);if(bytes.size()>1024*1024)throw new IOException("响应过大");}}
   JSONObject result=new JSONObject(bytes.toString("UTF-8"));
   if(code==401)throw new Unauthorized(result.optString("error","请重新配对"));
   if(code!=200)throw new IOException(result.optString("error","请求失败："+code));return result;
  }catch(SSLHandshakeException e){
   Throwable cause=e;while(cause!=null){if(cause.getMessage()!=null&&cause.getMessage().contains("证书与扫码内容不一致"))throw new Unauthorized("电脑服务已重启或证书不匹配，请重新扫码确认");cause=cause.getCause();}throw e;
  }catch(IOException e){if(path.startsWith("/poll")&&generation!=pollGeneration.get())throw new Reconnecting();throw e;}finally{if(pollConnection==conn)pollConnection=null;conn.disconnect();}
 }
 static HttpsURLConnection openVideo(Context context,String session)throws Exception{
  android.content.SharedPreferences p=context.getSharedPreferences("pair",0);String expected=p.getString("expectedCharacterKey","");JSONObject game=ChatService.gameAccount;if(!expected.isEmpty()&&(game==null||!expected.equals(game.optString("name")+"@"+game.optString("world"))))throw new IOException("电脑当前角色与所选账号不同");if(!session.matches("[a-f0-9]{32}"))throw new IOException("视频会话无效");HttpsURLConnection c=openPinned(p.getString("url",""),p.getString("pin",""),p.getString("token",""),"/stream-video?session="+session);c.setReadTimeout(5000);return c;
 }
 private static HttpsURLConnection openPinned(String address,String pin,String token,String path)throws Exception{
  URL url=new URL(address+path);
  if(!url.getProtocol().equals("https")||pin.length()!=64)throw new IOException("配对信息无效");
  TrustManager[] trust={new X509TrustManager(){
   public X509Certificate[] getAcceptedIssuers(){return new X509Certificate[0];}
   public void checkClientTrusted(X509Certificate[] c,String a)throws java.security.cert.CertificateException{throw new java.security.cert.CertificateException("不支持客户端证书");}
   public void checkServerTrusted(X509Certificate[] chain,String auth)throws java.security.cert.CertificateException{
    try{StringBuilder b=new StringBuilder();for(byte v:MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()))b.append(String.format("%02x",v&255));
     if(!MessageDigest.isEqual(b.toString().getBytes("UTF-8"),pin.getBytes("UTF-8")))throw new Exception("证书与扫码内容不一致，请重新配对");chain[0].checkValidity();
    }catch(Exception e){throw new java.security.cert.CertificateException(e);}
   }
  }};
  SSLContext ssl=SSLContext.getInstance("TLS");ssl.init(null,trust,null);
  HttpsURLConnection conn=(HttpsURLConnection)url.openConnection(Proxy.NO_PROXY);
  conn.setSSLSocketFactory(ssl.getSocketFactory());
  // Certificate identity is bound to the SHA-256 pin confirmed during device authorization.
  conn.setHostnameVerifier((host,session)->host.equals(url.getHost()));
  conn.setInstanceFollowRedirects(false);conn.setConnectTimeout(4500);conn.setReadTimeout(15000);
  if(!token.isEmpty())conn.setRequestProperty("Authorization","Bearer "+token);
  return conn;
 }
 static final class Reconnecting extends IOException {}
 static final class Unauthorized extends IOException {Unauthorized(String message){super(message);}}
}
