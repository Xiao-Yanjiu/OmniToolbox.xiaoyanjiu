package cn.xiaoyanjiu.ff14chat;
import android.app.*;import android.content.*;import android.content.pm.PackageManager;import android.os.Build;import org.json.*;import java.util.*;
final class FishingReminders {
 static final String PREF="fishing_reminders",CHANNEL="fishing_windows",ACTION="cn.xiaoyanjiu.ff14chat.FISH_ALARM";
 static boolean enabled(Context c){return c.getSharedPreferences(PREF,0).getBoolean("enabled",true);}
 static boolean notifications(Context c){NotificationManager manager=c.getSystemService(NotificationManager.class);NotificationChannel channel=manager.getNotificationChannel(CHANNEL);return (Build.VERSION.SDK_INT<33||c.checkSelfPermission("android.permission.POST_NOTIFICATIONS")==PackageManager.PERMISSION_GRANTED)&&manager.areNotificationsEnabled()&&(channel==null||channel.getImportance()!=NotificationManager.IMPORTANCE_NONE);}
 static boolean exact(Context c){return Build.VERSION.SDK_INT<31||c.getSystemService(AlarmManager.class).canScheduleExactAlarms();}
 static void channel(Context c){NotificationChannel n=new NotificationChannel(CHANNEL,"钓鱼时间提醒",NotificationManager.IMPORTANCE_HIGH);n.enableVibration(true);c.getSystemService(NotificationManager.class).createNotificationChannel(n);}
 static PendingIntent alarm(Context c){return PendingIntent.getBroadcast(c,8271,new Intent(c,FishingReminderReceiver.class).setAction(ACTION),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);}
 static String text(JSONObject o,String k,int max)throws Exception{String s=o.optString(k,"").trim();if(s.length()>max||s.indexOf('\0')>=0)throw new Exception("提醒内容过长");return s;}
 static JSONArray clean(JSONArray raw,long now)throws Exception{
  if(raw.length()>1000)throw new Exception("一次最多同步1000条提醒");List<JSONObject> rows=new ArrayList<>();Set<String> keys=new HashSet<>();
  for(int i=0;i<raw.length();i++){JSONObject o=raw.getJSONObject(i);String key=text(o,"key",140),title=text(o,"title",120),body=text(o,"body",500);long at=o.getLong("at"),expires=o.optLong("expires",at+900000);if(expires<at||expires>now+31L*86400000)throw new Exception("提醒有效期无效");if(key.isEmpty()||title.isEmpty())throw new Exception("提醒资料不完整");if(at<=now)continue;if(at>now+30L*86400000)throw new Exception("提醒超出30天范围");if(keys.add(key))rows.add(new JSONObject().put("key",key).put("title",title).put("body",body).put("at",at).put("expires",expires));}
  Collections.sort(rows,(a,b)->Long.compare(a.optLong("at"),b.optLong("at")));JSONArray out=new JSONArray();for(JSONObject o:rows)out.put(o);return out;
 }
 static JSONArray queue(Context c){try{return new JSONArray(c.getSharedPreferences(PREF,0).getString("queue","[]"));}catch(Exception e){return new JSONArray();}}
 static synchronized void sync(Context c,JSONArray rows)throws Exception{c.getSharedPreferences(PREF,0).edit().putString("queue",clean(rows,System.currentTimeMillis()).toString()).apply();schedule(c);FishingServer.changed(c);}
 static synchronized void setEnabled(Context c,boolean on){c.getSharedPreferences(PREF,0).edit().putBoolean("enabled",on).apply();schedule(c);FishingServer.changed(c);}
 static synchronized void clear(Context c){c.getSharedPreferences(PREF,0).edit().putString("queue","[]").apply();schedule(c);FishingServer.changed(c);}
 // Cancel legacy phone alarms: the game server is the sole schedule owner.
 static synchronized void schedule(Context c){c.getSystemService(AlarmManager.class).cancel(alarm(c));}
 static synchronized void deliver(Context c){schedule(c);}
 static synchronized void notify(Context c,String key,String title,String body){notify(c,key,title,body,false);}
 static synchronized void notify(Context c,String key,String title,String body,boolean test){
  if((!test&&!enabled(c))||!notifications(c))return;channel(c);String hash=java.util.UUID.nameUUIDFromBytes(key.getBytes(java.nio.charset.StandardCharsets.UTF_8)).toString();long now=System.currentTimeMillis();
  if(now-c.getSharedPreferences(PREF,0).getLong("sent_"+hash,0)<86400000)return;c.getSharedPreferences(PREF,0).edit().putLong("sent_"+hash,now).apply();
  PendingIntent open=PendingIntent.getActivity(c,key.hashCode(),new Intent(c,FishingActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
  Notification n=new Notification.Builder(c,CHANNEL).setSmallIcon(android.R.drawable.ic_popup_reminder).setContentTitle(title).setContentText(body).setStyle(new Notification.BigTextStyle().bigText(body)).setContentIntent(open).setAutoCancel(true).setCategory(Notification.CATEGORY_REMINDER).setVisibility(Notification.VISIBILITY_PRIVATE).build();
  try{c.getSystemService(NotificationManager.class).notify("fish:"+key,key.hashCode(),n);}catch(SecurityException ignored){}
 }
 static String summary(Context c){JSONArray q=queue(c);int count=0;long next=Long.MAX_VALUE;for(int i=0;i<q.length();i++){JSONObject o=q.optJSONObject(i);if(o!=null&&o.optLong("at")>System.currentTimeMillis()){count++;next=Math.min(next,o.optLong("at"));}}
  return (!enabled(c)?"手机提醒已暂停":!notifications(c)?"未允许通知":!exact(c)?"未允许准时提醒，系统可能延迟":"手机提醒已开启")+" · 已同步 "+count+" 条"+(next==Long.MAX_VALUE?"":"\n下次："+android.text.format.DateFormat.format("MM-dd HH:mm",next))+"\n再次打开鱼糕会更新后续窗口。";
 }
}
