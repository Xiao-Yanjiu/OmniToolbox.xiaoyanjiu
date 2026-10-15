package cn.xiaoyanjiu.ff14chat;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
final class AccountIdentity {
 static String key(String url,String pin,String token,String character)throws Exception{byte[] bytes=MessageDigest.getInstance("SHA-256").digest((url+"|"+pin+token+"|"+character).getBytes(StandardCharsets.UTF_8));StringBuilder out=new StringBuilder();for(byte b:bytes)out.append(String.format("%02x",b&255));return out.toString();}
}
