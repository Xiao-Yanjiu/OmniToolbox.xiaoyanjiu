package cn.xiaoyanjiu.ff14chat;
public class AccountIdentityRegression {
 public static void main(String[] args)throws Exception{
  String a=AccountIdentity.key("https://same-host:18765","pin","token-one","Player A@World");
  String b=AccountIdentity.key("https://same-host:18765","pin","token-one","Player B@World");
  if(a.equals(b))throw new AssertionError("Different characters sharing endpoint and authorization merged");
  if(a.equals(AccountIdentity.key("https://same-host:18765","pin","token-two","Player A@World")))throw new AssertionError("Distinct authorizations merged");
  if(a.equals(AccountIdentity.key("https://same-host:18765","other-pin","token-one","Player A@World")))throw new AssertionError("Different server certificate merged");
  if(!a.equals(AccountIdentity.key("https://same-host:18765","pin","token-one","Player A@World")))throw new AssertionError("Same saved account cannot be found after restart");
  if(a.length()!=64||a.contains("token"))throw new AssertionError("Public account identifier leaks raw credentials");
  System.out.println("PASS shared-endpoint character separation, credential separation, server identity, restart stability and opaque account ID");
 }
}
