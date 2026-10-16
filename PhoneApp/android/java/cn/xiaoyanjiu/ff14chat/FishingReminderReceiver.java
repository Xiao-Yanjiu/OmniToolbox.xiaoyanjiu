package cn.xiaoyanjiu.ff14chat;
import android.content.*;
public final class FishingReminderReceiver extends BroadcastReceiver {
 @Override public void onReceive(Context c,Intent i){if(FishingReminders.ACTION.equals(i.getAction()))FishingReminders.deliver(c);else FishingReminders.schedule(c);}
}
