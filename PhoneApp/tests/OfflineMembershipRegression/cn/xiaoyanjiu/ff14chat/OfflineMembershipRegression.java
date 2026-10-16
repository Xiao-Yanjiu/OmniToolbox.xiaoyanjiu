package cn.xiaoyanjiu.ff14chat;
import org.json.*;import java.net.*;public class OfflineMembershipRegression{
 static int checks;static void check(boolean value,String label){if(!value)throw new AssertionError(label);checks++;System.out.println("PASS "+label);}
 static class Connection extends HttpURLConnection{boolean cancelled;Connection()throws Exception{super(new URL("https://example.invalid/"));}public void connect(){}public void disconnect(){cancelled=true;}public boolean usingProxy(){return false;}}
 public static void main(String[] args)throws Exception{
  String a="a",b="b";check(!OfficeClient.joined(a)&&!OfficeClient.current(a,0),"new app session defaults to disconnected");
  JSONObject result=OfficeClient.call(null,a,"messages",new JSONObject(),0);
  check(result.optJSONObject("error").optString("code").equals("NOT_JOINED")&&AccountStore.reads==0,"disconnected chat is rejected before token or network access");
  result=OfficeClient.call(null,a,"posts",new JSONObject(),0);
  check(result.optJSONObject("error").optString("code").equals("NOT_JOINED"),"disconnected recruitment is rejected by the same gate");
  OfficeClient.enter(null,a);int epoch=OfficeClient.generation(a);check(OfficeClient.current(a,epoch),"explicit entry creates an active generation");
  Connection active=new Connection();OfficeClient.active.put(a,active);OfficeClient.leave(a);
  check(active.cancelled&&!OfficeClient.joined(a)&&!OfficeClient.current(a,epoch),"exit cancels in-flight HTTP and invalidates old queued requests");
  OfficeClient.enter(null,a);check(!OfficeClient.current(a,epoch),"re-entering cannot revive queued tasks from an earlier connection");
  int reads=AccountStore.reads;result=OfficeClient.call(null,a,"send",new JSONObject(),epoch);
  check(AccountStore.reads==reads&&result.optJSONObject("error").optString("code").equals("NOT_JOINED"),"stale queued send cannot access the network after re-entry");
  OfficeClient.enter(null,b);check(OfficeClient.joined(a)&&OfficeClient.joined(b),"accounts have separate native generations");
  OfficeClient.leave(a);check(OfficeClient.joined(b),"leaving one account cannot change another account");
  OfficeClient.leaveAll();check(!OfficeClient.joined(a)&&!OfficeClient.joined(b),"activity destruction clears every local connection");
  System.out.println(checks+" native membership checks; stubbed account/JSON/Android only, no network requests or messages sent.");
 }
}