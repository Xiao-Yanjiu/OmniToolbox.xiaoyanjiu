package cn.xiaoyanjiu.ff14chat;
import java.util.Arrays;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
public class AccountCipherRegression {
 public static void main(String[] args)throws Exception{
  KeyGenerator generator=KeyGenerator.getInstance("AES");generator.init(256);SecretKey key=generator.generateKey();
  String record="{\"name\":\"小烟酒\",\"alias\":\"12345\",\"token\":\"fixture-authorization-token\"}";
  record=record.substring(0,record.length()-1)+",\"officeToken\":\"OST_fixture_offline_secret\"}";
  byte[] bytes=AccountCipher.encrypt(record,key);if(!record.equals(AccountCipher.decrypt(bytes,key)))throw new AssertionError("Unicode account round trip failed");
  System.out.println("PASS authorized account and Unicode identity survive encrypted round trip");
  if(new String(bytes,"ISO-8859-1").contains("fixture-authorization-token")||new String(bytes,"ISO-8859-1").contains("OST_fixture_offline_secret"))throw new AssertionError("Plain token leaked");
  if(Arrays.equals(bytes,AccountCipher.encrypt(record,key)))throw new AssertionError("GCM nonce reused");System.out.println("PASS credentials hidden and each save uses a fresh nonce");
  byte[] changed=bytes.clone();changed[changed.length-1]^=1;try{AccountCipher.decrypt(changed,key);throw new AssertionError("Tampering accepted");}catch(javax.crypto.AEADBadTagException expected){}
  try{AccountCipher.decrypt(bytes,generator.generateKey());throw new AssertionError("Wrong key accepted");}catch(javax.crypto.AEADBadTagException expected){}
  try{AccountCipher.decrypt(new byte[12],key);throw new AssertionError("Truncated data accepted");}catch(java.io.IOException expected){}
  System.out.println("PASS tampered, wrong-key and truncated account records are rejected");
 }
}
