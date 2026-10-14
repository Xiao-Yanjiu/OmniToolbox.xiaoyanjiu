package cn.xiaoyanjiu.ff14chat;

import java.io.IOException;
import javax.crypto.Cipher;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

final class AccountCipher {
 static byte[] encrypt(String json,SecretKey key)throws Exception{
  Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key);
  byte[] iv=cipher.getIV(),data=cipher.doFinal(json.getBytes("UTF-8"));if(iv.length!=12)throw new IOException("Unexpected GCM nonce size");
  byte[] output=new byte[iv.length+data.length];System.arraycopy(iv,0,output,0,iv.length);System.arraycopy(data,0,output,iv.length,data.length);return output;
 }
 static String decrypt(byte[] bytes,SecretKey key)throws Exception{
  if(bytes.length<28||bytes.length>256*1024)throw new IOException("账号记录损坏");
  Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key,new GCMParameterSpec(128,bytes,0,12));return new String(cipher.doFinal(bytes,12,bytes.length-12),"UTF-8");
 }
}
