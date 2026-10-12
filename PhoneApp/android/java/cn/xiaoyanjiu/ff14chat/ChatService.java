package cn.xiaoyanjiu.ff14chat;

import android.app.*;
import android.content.*;
import android.os.*;
import android.net.*;
import org.json.*;
import java.util.concurrent.*;

public class ChatService extends Service {
 static final Object LOCK=new Object();
 static JSONArray filterOptions=new JSONArray();
 static JSONArray channels=new JSONArray();
 static ChatStore store;
 static SharedPreferences receiverState;
 static void prepareStore(Context context){synchronized(LOCK){if(store==null){store=new ChatStore(context.getApplicationContext());receiverState=context.getSharedPreferences("receiver",0);gameId=receiverState.getString("gameId","");historyVersion=receiverState.getString("historyVersion","");noticeCursor=receiverState.getLong("noticeCursor",0);if(!gameId.isEmpty())appliedInitialization=0;}}}
 private static void persistReceiver(){if(receiverState!=null)receiverState.edit().putString("gameId",gameId).putString("historyVersion",historyVersion).putLong("noticeCursor",noticeCursor).apply();}
 static JSONArray tabs=new JSONArray(),macros=new JSONArray(),commands=new JSONArray();
 static JSONArray channelInfo=new JSONArray();
 static boolean worldJoined=false,noviceJoined=false;
 static String metadataError="";static String connectionError="";static int disconnectCount;
 static volatile String state="未连接";
 static volatile boolean visible=false,allowCommands=false;
 static final ExecutorService SEND=Executors.newSingleThreadExecutor();
 private volatile boolean running;
 private Thread worker;
 private final Object retrySignal=new Object();
 private volatile long lastProgress=android.os.SystemClock.elapsedRealtime();
 private ConnectivityManager networkManager;
 private ConnectivityManager.NetworkCallback networkCallback;
 private volatile Network activeNetwork;
 private final ExecutorService recoveryExecutor=Executors.newSingleThreadExecutor();
 private final java.util.concurrent.atomic.AtomicBoolean recoveryPending=new java.util.concurrent.atomic.AtomicBoolean();
 private BroadcastReceiver wakeReceiver;
 private final Handler recoveryHandler=new Handler(android.os.Looper.getMainLooper());
 private final Runnable watchdog=new Runnable(){public void run(){if(running){if(worker==null||!worker.isAlive())startWorker();else if(android.os.SystemClock.elapsedRealtime()-lastProgress>60000)recover();recoveryHandler.postDelayed(this,20000);}}};
 private synchronized void startWorker(){if(worker!=null&&worker.isAlive())return;running=true;lastProgress=android.os.SystemClock.elapsedRealtime();worker=new Thread(this::loop,"PhoneChat-poll");worker.start();}
 private void recover(){if(!running||!recoveryPending.compareAndSet(false,true))return;try{recoveryExecutor.execute(()->{try{Transport.cancelPoll();synchronized(retrySignal){retrySignal.notifyAll();}if(running&&(worker==null||!worker.isAlive()))startWorker();}finally{recoveryPending.set(false);}});}catch(RejectedExecutionException stopped){recoveryPending.set(false);}}

 private PowerManager.WakeLock receiveLock;
 private static boolean appStarted;
 private static volatile int appliedInitialization=-1;
 static void initializeForAppStart(Context context){prepareStore(context);synchronized(LOCK){if(appStarted)return;initialize();appStarted=true;}}
 static final java.util.concurrent.atomic.AtomicInteger initialization=new java.util.concurrent.atomic.AtomicInteger();
 static void initialize(){synchronized(LOCK){clearSession();initialization.incrementAndGet();state="初始化 · 正在读取游戏聊天记录…";}}
 private long after;private long routeAfter;private boolean routesPending;
 static long noticeCursor;static String gameId="",historyVersion="";static int historyOffset;static int filteredCount;static String latestPageTime="";static long latestPageId;
 private final java.util.concurrent.atomic.AtomicInteger noticeId=new java.util.concurrent.atomic.AtomicInteger(100);
 public IBinder onBind(Intent i){return null;}
 @Override public void onCreate(){super.onCreate();try{prepareStore(this);}catch(Exception e){state="聊天缓存初始化失败："+e.getMessage();}
  NotificationManager nm=getSystemService(NotificationManager.class);
  nm.createNotificationChannel(new NotificationChannel("connection","连接状态",NotificationManager.IMPORTANCE_LOW));
  nm.createNotificationChannel(new NotificationChannel("chat","悄悄话与 @ 提醒",NotificationManager.IMPORTANCE_HIGH));
 }
 private PendingIntent open(){return PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);}
 private Notification connection(){return new Notification.Builder(this,"connection").setSmallIcon(android.R.drawable.ic_dialog_email).setContentTitle("小烟酒").setContentText(state).setContentIntent(open()).setOngoing(true).setOnlyAlertOnce(true).build();}
 private void update(String text){if(text.equals(state))return;state=text;getSystemService(NotificationManager.class).notify(1,connection());}
 @Override public int onStartCommand(Intent intent,int flags,int startId){
  startForeground(1,connection());
  if(store==null){stopSelf();return START_NOT_STICKY;}
  if(getSharedPreferences("pair",0).getString("token","").isEmpty()){stopSelf();return START_NOT_STICKY;}
  if(receiveLock==null){receiveLock=getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"FF14Chat:Receive");receiveLock.setReferenceCounted(false);}
  if(!receiveLock.isHeld())receiveLock.acquire();
  if(intent!=null&&"reply".equals(intent.getAction())){
   Bundle result=RemoteInput.getResultsFromIntent(intent);CharSequence text=result==null?null:result.getCharSequence("reply");
   if(text!=null){String channel=intent.getStringExtra("channel"),target=intent.getStringExtra("target");SEND.execute(()->{try{Transport.request(this,"/send",new JSONObject().put("channel",channel).put("target",target).put("text",text.toString()));}catch(Exception e){errorNotice(e.getMessage());}});}
  }
  if(!running||worker==null||!worker.isAlive()){synchronized(LOCK){after=store.lastId();}startWorker();recoveryHandler.removeCallbacks(watchdog);recoveryHandler.postDelayed(watchdog,20000);}
  if(intent!=null&&"recover".equals(intent.getAction())&&android.os.SystemClock.elapsedRealtime()-lastProgress>35000)recover();
  if(networkCallback==null){try{networkManager=getSystemService(ConnectivityManager.class);networkCallback=new ConnectivityManager.NetworkCallback(){@Override public void onAvailable(Network network){activeNetwork=network;recover();}@Override public void onLost(Network network){if(network.equals(activeNetwork)){activeNetwork=null;recover();}}@Override public void onCapabilitiesChanged(Network network,NetworkCapabilities caps){if(caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)&&android.os.SystemClock.elapsedRealtime()-lastProgress>35000)recover();}};networkManager.registerDefaultNetworkCallback(networkCallback);}catch(Exception ignored){networkCallback=null;}}

  if(wakeReceiver==null){wakeReceiver=new BroadcastReceiver(){@Override public void onReceive(Context context,Intent intent){recover();}};IntentFilter filter=new IntentFilter(Intent.ACTION_SCREEN_ON);filter.addAction(Intent.ACTION_USER_PRESENT);filter.addAction(PowerManager.ACTION_POWER_SAVE_MODE_CHANGED);if(android.os.Build.VERSION.SDK_INT>=33)registerReceiver(wakeReceiver,filter,Context.RECEIVER_NOT_EXPORTED);else registerReceiver(wakeReceiver,filter);}
  return getSharedPreferences("pair",0).getString("token","").isEmpty()?START_NOT_STICKY:START_STICKY;
 }
 private void loop(){int activeInitialization=appliedInitialization;int failures=0;boolean first=store.count()==0;boolean noticesReady=!gameId.isEmpty();update("连接中…");
  while(running){try{
   int requested=initialization.get();if(activeInitialization!=requested){after=0;routeAfter=0;routesPending=false;first=true;noticesReady=false;Transport.request(this,"/reload",new JSONObject());if(!running)break;if(initialization.get()!=requested)continue;activeInitialization=requested;appliedInitialization=requested;}
   JSONObject response=Transport.request(this,"/poll?after="+after+"&routeAfter="+routeAfter+"&noticeAfter="+noticeCursor+"&initial="+((first||routesPending)?1:0),null);
   if(!running)break;if(initialization.get()!=activeInitialization)continue;
   String incomingVersion=response.optString("historyVersion");String incomingGame=response.optString("gameId");synchronized(LOCK){if(!incomingGame.isEmpty()&&(!incomingGame.equals(gameId)||!incomingVersion.equals(historyVersion))){boolean newGame=!incomingGame.equals(gameId);long savedNotice=noticeCursor;clearSession();if(!newGame)noticeCursor=savedNotice;else noticesReady=false;gameId=incomingGame;historyVersion=incomingVersion;after=0;routeAfter=0;routesPending=false;first=true;if(response.optJSONArray("messages")!=null&&response.optJSONArray("messages").length()>0&&response.optJSONArray("messages").optJSONObject(0).optLong("id")>1)continue;}}
   JSONArray batch=response.optJSONArray("messages");synchronized(LOCK){filterOptions=response.optJSONArray("filterOptions");if(filterOptions==null)filterOptions=new JSONArray();channels=response.optJSONArray("channels");if(channels==null)channels=new JSONArray();allowCommands=response.optBoolean("allowCommands");}
   synchronized(LOCK){tabs=response.optJSONArray("tabs");if(tabs==null)tabs=new JSONArray();macros=response.optJSONArray("macros");if(macros==null)macros=new JSONArray();commands=response.optJSONArray("commands");if(commands==null)commands=new JSONArray();channelInfo=response.optJSONArray("channelInfo");if(channelInfo==null)channelInfo=new JSONArray();worldJoined=response.optBoolean("worldJoined");noviceJoined=response.optBoolean("noviceJoined");metadataError=response.optString("metadataError");}
   java.util.ArrayList<JSONObject> fresh=new java.util.ArrayList<>();long committedAfter=after;
   if(batch!=null)synchronized(LOCK){store.begin();try{for(int i=0;i<batch.length();i++){
    JSONObject entry=batch.getJSONObject(i);long id=entry.optLong("id");if(id<=committedAfter)continue;
    if(store.put(entry))fresh.add(entry);committedAfter=id;
   }store.commit();after=committedAfter;}catch(Exception e){store.rollback();throw e;}}
   for(JSONObject entry:fresh)if(!first&&!visible&&entry.optBoolean("notify")&&!entry.optBoolean("outgoing"))notifyMessage(entry);
   JSONArray notices=response.optJSONArray("notices");if(notices!=null)for(int i=0;i<notices.length();i++){JSONObject entry=notices.getJSONObject(i);long id=entry.optLong("id");if(id<=noticeCursor)continue;noticeCursor=id;if(noticesReady&&!visible&&!entry.optBoolean("externalPush"))notifyMessage(entry);}noticesReady=true;persistReceiver();
   if(!response.optBoolean("hasMore")&&!response.optBoolean("historyLoading"))first=false;failures=0;lastProgress=android.os.SystemClock.elapsedRealtime();update((response.optBoolean("hasMore")||response.optBoolean("historyLoading"))?"已连接 · 加载游戏聊天历史…":"已连接 · 游戏聊天历史已同步");
   JSONArray routes=response.optJSONArray("routes");if(routes!=null)synchronized(LOCK){store.begin();try{for(int i=0;i<routes.length();i++){JSONObject route=routes.getJSONObject(i);store.routes(route.optLong("id"),route.optJSONArray("tabs"),true);}store.commit();}catch(Exception e){store.rollback();throw e;}}
   routesPending=response.optBoolean("hasMoreRoutes");routeAfter=routesPending?response.optLong("nextRouteAfter"):0;
  }catch(Transport.Unauthorized e){if(!running)break;getSharedPreferences("pair",0).edit().clear().apply();update("设备授权失效，请重新连接");running=false;new Handler(getMainLooper()).post(()->{stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();});}
   catch(Exception e){if(!running)break;synchronized(LOCK){connectionError=e.getClass().getSimpleName()+": "+String.valueOf(e.getMessage());disconnectCount++;}update("连接断开，正在重试 · "+connectionError);try{synchronized(retrySignal){if(running)retrySignal.wait(Math.min(15000,1000L*(1L<<Math.min(failures++,4))));}}catch(InterruptedException stop){if(!running)break;}}
  }
 }
 private void notifyMessage(JSONObject entry){
  try{String channel=entry.getString("channel"),target=entry.optString("target");String replyChannel=channel;
   if(channel.equals("国服")){replyChannel="国服";String sender=entry.optString("sender");java.util.regex.Matcher m=java.util.regex.Pattern.compile("（(\\d+)）$").matcher(sender);if(m.find())target=m.group(1);}
   Notification.Builder b=new Notification.Builder(this,"chat").setSmallIcon(android.R.drawable.ic_dialog_email).setContentTitle("["+channel+"] "+entry.optString("sender")).setContentText(entry.optString("text")).setStyle(new Notification.BigTextStyle().bigText(entry.optString("text"))).setContentIntent(open()).setAutoCancel(true);
   int id=noticeId.incrementAndGet();
   if(!target.isEmpty()){
    Intent reply=new Intent(this,ChatService.class).setAction("reply").putExtra("channel",replyChannel).putExtra("target",target);
    PendingIntent pi=PendingIntent.getForegroundService(this,id,reply,PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_MUTABLE);
    b.addAction(new Notification.Action.Builder(android.R.drawable.ic_menu_send,"回复",pi).addRemoteInput(new RemoteInput.Builder("reply").setLabel("消息内容").build()).build());
   }
   getSystemService(NotificationManager.class).notify(id,b.build());
  }catch(Exception ignored){}
 }
 private void errorNotice(String text){getSystemService(NotificationManager.class).notify(2,new Notification.Builder(this,"chat").setSmallIcon(android.R.drawable.ic_dialog_alert).setContentTitle("发送失败").setContentText(text).setContentIntent(open()).build());}
 static void history(String direction){synchronized(LOCK){if("older".equals(direction)&&historyOffset==0&&store!=null)store.anchor(latestPageTime,latestPageId);int max=Math.max(0,filteredCount-1);historyOffset=direction.equals("older")?Math.min(max,historyOffset+200):direction.equals("newer")?Math.max(0,historyOffset-200):0;if(historyOffset==0&&store!=null)store.anchor("",0);}}
 static String snapshot(){return snapshotFor("全部","frames","");}
 static String snapshotFor(String current,String view,String thread){return snapshotSelected(current,view,thread,"null");}
 static String snapshotSelected(String current,String view,String thread,String selected){synchronized(LOCK){try{
  if(store==null)return new JSONObject().put("state",state).put("messages",new JSONArray()).toString();if(current==null)current="全部";if(view==null)view="frames";if(thread==null)thread="";
  String[] selection=null;if("全部".equals(current)&&selected!=null&&!"null".equals(selected)){JSONArray choices=new JSONArray(selected);selection=new String[Math.min(choices.length(),1024)];for(int i=0;i<selection.length;i++)selection[i]=choices.optString(i);}
  ChatStore.Page page=store.selectedPage(current,view,thread,historyOffset,selection);filteredCount=page.filtered;if(historyOffset==0){latestPageTime=page.latestTime;latestPageId=page.latestId;}
  return new JSONObject().put("state",state).put("messages",page.messages).put("totalMessages",page.total).put("filteredMessages",page.filtered).put("earliestTime",page.earliest).put("displayStart",page.start+1).put("displayEnd",page.end).put("hasOlder",page.start>0).put("hasNewer",historyOffset>0).put("historyOffset",historyOffset).put("channels",channels).put("filterOptions",filterOptions).put("allowCommands",allowCommands).put("tabs",tabs).put("macros",macros).put("commands",commands).put("channelInfo",channelInfo).put("worldJoined",worldJoined).put("noviceJoined",noviceJoined).put("metadataError",metadataError).put("connectionError",connectionError).put("disconnectCount",disconnectCount).toString();}catch(Exception e){connectionError="读取聊天缓存："+e.getMessage();return "{}";}}}
 static void clearSession(){synchronized(LOCK){if(store!=null)store.clear();noticeCursor=0;persistReceiver();historyOffset=0;filteredCount=0;latestPageTime="";latestPageId=0;channels=new JSONArray();tabs=new JSONArray();macros=new JSONArray();commands=new JSONArray();channelInfo=new JSONArray();allowCommands=false;worldJoined=false;noviceJoined=false;metadataError="";}}
 @Override public void onTaskRemoved(Intent rootIntent){visible=false;if(running)recover();super.onTaskRemoved(rootIntent);}
 @Override public void onDestroy(){running=false;if(wakeReceiver!=null)try{unregisterReceiver(wakeReceiver);}catch(Exception ignored){}recoveryHandler.removeCallbacks(watchdog);if(networkManager!=null&&networkCallback!=null)try{networkManager.unregisterNetworkCallback(networkCallback);}catch(Exception ignored){}try{recoveryExecutor.execute(Transport::cancelPoll);}catch(RejectedExecutionException ignored){}recoveryExecutor.shutdown();synchronized(retrySignal){retrySignal.notifyAll();}if(worker!=null)worker.interrupt();if(receiveLock!=null&&receiveLock.isHeld())receiveLock.release();super.onDestroy();}
}



