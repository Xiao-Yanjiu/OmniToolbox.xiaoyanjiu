package cn.xiaoyanjiu.ff14chat;
import android.content.*;import org.json.*;import java.util.concurrent.atomic.AtomicBoolean;
final class FishingServer {
 static final AtomicBoolean busy=new AtomicBoolean();static volatile long tried;static volatile boolean dirty=true;
 static android.content.SharedPreferences prefs(Context c){return c.getSharedPreferences(FishingReminders.PREF,0);}
 static int mode(Context c){return prefs(c).getInt("serverRoute",0);}
 static void mode(Context c,int value){prefs(c).edit().putInt("serverRoute",value).apply();changed(c);}
 static void changed(Context c){dirty=true;flush(c,true);}
 static void flush(Context source,boolean force){Context c=source.getApplicationContext();if(!ChatService.pageEnabled("fishing"))return;if(!force&&!dirty&&System.currentTimeMillis()-tried<60000)return;if(!busy.compareAndSet(false,true))return;
  ChatService.SEND.execute(()->{try{tried=System.currentTimeMillis();dirty=false;
   if(c.getSharedPreferences("pair",0).getString("token","").isEmpty())throw new Exception("请先连接游戏内服务端");
   JSONArray rows=FishingReminders.clean(FishingReminders.queue(c),System.currentTimeMillis());
   int requestedMode=mode(c),effectiveMode=requestedMode==0&&!FishingReminders.notifications(c)?2:requestedMode;
   JSONObject result=Transport.request(c,"/fishing-reminders",new JSONObject().put("action","sync").put("rows",rows).put("enabled",FishingReminders.enabled(c)).put("mode",effectiveMode));
   if(!result.optBoolean("ok"))throw new Exception("请更新服务端至0.3.75");
   String route=new String[]{"自动：App在线直连，离线使用QQ／推送","仅App通知","QQ／推送服务"}[mode(c)];
   if(requestedMode==0&&effectiveMode==2)route+="\nApp通知未允许，自动使用QQ／推送";
   String state=(FishingReminders.enabled(c)?"服务端提醒已开启":"服务端提醒已暂停")+" · 已同步 "+result.optInt("count")+" 条";
   long next=result.optLong("next");prefs(c).edit().putString("serverStatus",state).putString("serverDetail",route+(next>0?"\n下次："+android.text.format.DateFormat.format("MM-dd HH:mm",next):"")+"\n"+result.optString("status")+"\n服务端须保持运行；再次打开鱼糕更新后续窗口。").apply();
  }catch(Exception e){prefs(c).edit().putString("serverStatus","服务端未同步："+e.getMessage()).apply();dirty=true;}finally{busy.set(false);}});
 }
 static String summary(Context c){return prefs(c).getString("serverStatus","等待同步到服务端")+"\n"+prefs(c).getString("serverDetail","请使用0.3.75及以上游戏内服务端");}
 static void notify(Context c,JSONObject data){if(!FishingReminders.enabled(c))return;ChatService.SEND.execute(()->{try{Transport.request(c,"/fishing-reminders",new JSONObject().put("action","notify").put("title",FishingReminders.text(data,"title",120)).put("body",FishingReminders.text(data,"body",500)));}catch(Exception e){prefs(c).edit().putString("serverStatus","即时提醒发送失败："+e.getMessage()).apply();}});}
 static void test(Context c){ChatService.SEND.execute(()->{try{
  JSONObject result=Transport.request(c,"/fishing-reminders",new JSONObject().put("action","test"));JSONObject notice=result.optJSONObject("testNotice");if(notice==null)throw new Exception("请重载0.3.76服务端后再测试");
  android.os.Handler h=new android.os.Handler(c.getMainLooper());h.post(()->{
   if(FishingReminders.notifications(c)){FishingReminders.notify(c,notice.optString("key"),notice.optString("title"),notice.optString("body"),true);android.widget.Toast.makeText(c,"服务端测试通知已触发",1).show();}
   else if(c instanceof android.app.Activity&&!((android.app.Activity)c).isFinishing())new android.app.AlertDialog.Builder(c).setTitle("鱼糕测试通知").setMessage("服务端已收到测试请求，但手机的钓鱼通知权限已关闭。请允许通知，并在系统中开启横幅／悬浮提醒。").setPositiveButton("通知设置",(d,n)->c.startActivity(new Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE,c.getPackageName()).putExtra(android.provider.Settings.EXTRA_CHANNEL_ID,FishingReminders.CHANNEL))).setNegativeButton("关闭",null).show();
  });
 }catch(Exception e){String message="测试失败："+e.getMessage();prefs(c).edit().putString("serverStatus",message).apply();new android.os.Handler(c.getMainLooper()).post(()->{if(c instanceof android.app.Activity&&!((android.app.Activity)c).isFinishing())new android.app.AlertDialog.Builder(c).setTitle("鱼糕测试通知").setMessage(message).setPositiveButton("关闭",null).show();else android.widget.Toast.makeText(c,message,1).show();});}});}
}
