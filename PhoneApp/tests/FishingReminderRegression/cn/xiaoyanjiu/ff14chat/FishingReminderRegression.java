package cn.xiaoyanjiu.ff14chat;import org.json.*;public class FishingReminderRegression{
 static int checks;static long now=1792100000000L;
 static void check(boolean b,String s){if(!b)throw new AssertionError(s);checks++;System.out.println("PASS "+s);}
 static JSONObject row(String key,long at){return new JSONObject().put("key",key).put("title","人面鲤 即将可钓").put("body","镜池").put("at",at).put("url","https://untrusted.invalid/");}
 static void reject(JSONArray rows,String label)throws Exception{try{FishingReminders.clean(rows,now);throw new AssertionError(label);}catch(AssertionError e){throw e;}catch(Exception e){check(true,label);}}
 public static void main(String[] args)throws Exception{
  JSONArray q=new JSONArray().put(row("later",now+20000)).put(row("first",now+10000)).put(row("first",now+10000)).put(row("past",now-1));
  JSONArray clean=FishingReminders.clean(q,now);check(clean.length()==2,"past events and duplicate identities do not create extra notifications");
  check(clean.getJSONObject(0).optString("key").equals("first"),"next reminder is sorted ahead of later windows");
  check(!clean.getJSONObject(0).has("url"),"remote payload cannot inject a notification launch target");
  check(clean.getJSONObject(0).optString("title").contains("人面鲤"),"fish titles remain intact");
  reject(new JSONArray().put(row("far",now+31L*86400000)),"events beyond bounded scheduling horizon are rejected");
  reject(new JSONArray().put(row("",now+1000)),"empty notification identity is rejected");
  reject(new JSONArray().put(row("huge",now+1000).put("body","x".repeat(501))),"oversized notification text is rejected");
  JSONArray huge=new JSONArray();for(int i=0;i<1001;i++)huge.put(row("x"+i,now+1000));reject(huge,"oversized scheduling batches are rejected");
  reject(new JSONArray().put(row("bad-expiry",now+1000).put("expires",now+500)),"expiry before firing time is rejected");
  check(FishingReminders.clean(new JSONArray(),now).length()==0,"clearing website subscriptions produces an empty native plan");
  System.out.println(checks+" native reminder validation checks; Android JSON DTO stubs only, no real alarms or notifications emitted.");
 }
}