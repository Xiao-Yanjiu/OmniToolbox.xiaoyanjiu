package cn.xiaoyanjiu.ff14chat;

import android.content.*;
import android.database.*;
import android.database.sqlite.*;
import org.json.*;

// Historical rows stay on disk. Only a requested page is decoded into Java objects.
final class ChatStore {
 private final SQLiteDatabase db;
 private String anchorTime="";private long anchorId;
 void anchor(String time,long id){anchorTime=time;anchorId=id;}
 ChatStore(Context context){
  db=context.openOrCreateDatabase("chat-cache.db",0,null);
  db.enableWriteAheadLogging();
  db.execSQL("CREATE TABLE IF NOT EXISTS messages(id INTEGER PRIMARY KEY,time TEXT,channel TEXT,target TEXT,payload TEXT)");
  if(db.getVersion()<2){db.execSQL("ALTER TABLE messages ADD COLUMN record_key TEXT NOT NULL DEFAULT ''");db.execSQL("ALTER TABLE messages ADD COLUMN record_source TEXT NOT NULL DEFAULT ''");db.setVersion(2);}
  if(db.getVersion()<3){db.beginTransaction();try{db.execSQL("ALTER TABLE messages ADD COLUMN kind INTEGER NOT NULL DEFAULT 0");db.execSQL("ALTER TABLE messages ADD COLUMN source_kind INTEGER NOT NULL DEFAULT 0");db.execSQL("ALTER TABLE messages ADD COLUMN target_kind INTEGER NOT NULL DEFAULT 0");db.execSQL("ALTER TABLE messages ADD COLUMN system_origin TEXT NOT NULL DEFAULT 'unknown'");try(Cursor rows=db.rawQuery("SELECT id,payload FROM messages",null)){while(rows.moveToNext()){JSONObject entry=new JSONObject(rows.getString(1));ContentValues values=new ContentValues();filterValues(values,entry);db.update("messages",values,"id=?",new String[]{rows.getString(0)});}}db.setVersion(3);db.setTransactionSuccessful();}catch(JSONException e){throw new IllegalStateException(e);}finally{db.endTransaction();}}
  db.execSQL("CREATE INDEX IF NOT EXISTS by_record ON messages(record_key,record_source)");
  db.execSQL("CREATE INDEX IF NOT EXISTS chronological ON messages(time,id)");
  db.execSQL("CREATE INDEX IF NOT EXISTS by_channel ON messages(channel,time,id)");
  db.execSQL("CREATE TABLE IF NOT EXISTS routes(message_id INTEGER,tab TEXT,PRIMARY KEY(message_id,tab))");
  db.execSQL("CREATE INDEX IF NOT EXISTS by_tab ON routes(tab,message_id)");
 }
 void clear(){db.beginTransaction();try{db.delete("routes",null,null);db.delete("messages",null,null);db.setTransactionSuccessful();}finally{db.endTransaction();}try{checkpoint();db.execSQL("VACUUM");checkpoint();}catch(SQLiteException maintenance){android.util.Log.w("PhoneChat","缓存空间回收暂未完成",maintenance);}anchorTime="";anchorId=0;}
 private void checkpoint(){try(Cursor cursor=db.rawQuery("PRAGMA wal_checkpoint(TRUNCATE)",null)){cursor.moveToFirst();}}
 long lastId(){try(Cursor c=db.rawQuery("SELECT coalesce(max(id),0) FROM messages",null)){return c.moveToFirst()?c.getLong(0):0;}}
 int count(){try(Cursor c=db.rawQuery("SELECT count(*) FROM messages",null)){return c.moveToFirst()?c.getInt(0):0;}}
 boolean contains(long id){try(Cursor c=db.rawQuery("SELECT id FROM messages WHERE id=?",new String[]{String.valueOf(id)})){return c.moveToFirst();}}
 void begin(){db.beginTransaction();}void commit(){try{db.setTransactionSuccessful();}finally{db.endTransaction();}}void rollback(){if(db.inTransaction())db.endTransaction();}
 boolean put(JSONObject entry){
  long id=entry.optLong("id");boolean fresh=!contains(id);
  ContentValues row=new ContentValues();row.put("id",id);row.put("time",entry.optString("time"));row.put("channel",entry.optString("channel"));row.put("target",entry.optString("target"));row.put("payload",entry.toString());row.put("record_key",entry.optString("recordKey"));row.put("record_source",entry.optString("recordSource"));
  filterValues(row,entry);
  if(db.insertWithOnConflict("messages",null,row,SQLiteDatabase.CONFLICT_REPLACE)<0)throw new SQLiteException("聊天缓存写入失败");
  routes(id,entry.optJSONArray("tabs"),false);return fresh;
 }
 void routes(long id,JSONArray tabs,boolean updatePayload) throws RuntimeException {
  db.delete("routes","message_id=?",new String[]{String.valueOf(id)});
  if(tabs!=null)for(int j=0;j<tabs.length();j++){ContentValues row=new ContentValues();row.put("message_id",id);row.put("tab",tabs.optString(j));if(db.insertWithOnConflict("routes",null,row,SQLiteDatabase.CONFLICT_REPLACE)<0)throw new SQLiteException("聊天分类写入失败");}
  if(updatePayload)try(Cursor c=db.rawQuery("SELECT payload FROM messages WHERE id=?",new String[]{String.valueOf(id)})){
   if(c.moveToFirst()){JSONObject entry=new JSONObject(c.getString(0));entry.put("tabs",tabs==null?new JSONArray():tabs);ContentValues row=new ContentValues();row.put("payload",entry.toString());db.update("messages",row,"id=?",new String[]{String.valueOf(id)});}
  }catch(JSONException e){throw new IllegalStateException(e);}
 }
 private static void filterValues(ContentValues values,JSONObject entry){values.put("kind",entry.optInt("kind"));values.put("source_kind",entry.optInt("sourceKind"));values.put("target_kind",entry.optInt("targetKind"));values.put("system_origin",entry.optString("systemOrigin","unknown"));}
 static final class Filter {
  private static String rule(String token,java.util.ArrayList<String> args){
   if(token.startsWith("k:")||token.startsWith("n:")){try{String[] parts=token.substring(2).split(",");java.util.ArrayList<String> kinds=new java.util.ArrayList<>();for(String part:parts){int kind=Integer.parseInt(part);if(kind<0||kind>65535)return "0=1";kinds.add(String.valueOf(kind));}if(kinds.isEmpty())return "0=1";return token.startsWith("k:")?"m.kind IN ("+String.join(",",kinds)+")":"((m.channel LIKE '系统 /%' OR m.channel='默语') AND m.kind NOT IN ("+String.join(",",kinds)+"))";}catch(NumberFormatException e){return "0=1";}}
   if(token.startsWith("o:")){String[] parts=token.split("\\|",2);if(parts.length!=2)return "0=1";String origin=parts[0].substring(2);if(!origin.equals("game")&&!origin.equals("plugin")&&!origin.equals("unknown"))return "0=1";return "(m.system_origin='"+origin+"' AND "+rule(parts[1],args)+")";}
   if(token.startsWith("f:")){String[] parts=token.split(":");if(parts.length!=4)return "0=1";try{int kind=Integer.parseInt(parts[1]),source=Integer.parseInt(parts[2]),target=Integer.parseInt(parts[3]);if(kind<0||kind>65535||source<0||source>65535||target<0||target>65535)return "0=1";return "("+(kind==12?"m.kind IN (12,13)":"m.kind="+kind)+" AND m.source_kind>=0 AND m.source_kind<12 AND m.target_kind>=0 AND m.target_kind<12 AND ((1 << m.source_kind)&"+source+")<>0 AND ((1 << m.target_kind)&"+target+")<>0)";}catch(NumberFormatException e){return "0=1";}}
   String channel=token.startsWith("c:")?token.substring(2):token;
   if(channel.equals("系统消息"))return "m.channel LIKE '系统 /%'";
   if(channel.equals("游戏系统消息"))return "(m.channel LIKE '系统 /%' AND m.system_origin='game')";
   if(channel.equals("卫月插件消息"))return "((m.channel LIKE '系统 /%' OR m.channel='默语') AND m.system_origin='plugin')";
   if(channel.equals("来源未标注"))return "(m.channel LIKE '系统 /%' AND m.system_origin='unknown')";
   args.add(channel);return "m.channel=?";
  }
  String where;String[] args;
  Filter(String current,String view,String thread){this(current,view,thread,null);}
  Filter(String current,String view,String thread,String[] selection){java.util.ArrayList<String> parameters=new java.util.ArrayList<>();where="1=1";
   if("frames".equals(view)){where="EXISTS(SELECT 1 FROM routes r WHERE r.message_id=m.id";if(!"全部".equals(current)){where+=" AND r.tab=?";parameters.add(current);}where+=")";}
   else if("系统消息".equals(current))where="m.channel LIKE '系统 /%'";
   else if(!"全部".equals(current)){where="m.channel=?";parameters.add(current);}
   if("全部".equals(current)&&selection!=null){
    if(selection.length==0)where="0=1";
    else if("frames".equals(view)){String placeholders=String.join(",",java.util.Collections.nCopies(selection.length,"?"));where="EXISTS(SELECT 1 FROM routes chosen WHERE chosen.message_id=m.id AND chosen.tab IN ("+placeholders+"))";java.util.Collections.addAll(parameters,selection);
     where+=" AND (m.record_source<>'native' OR NOT EXISTS(SELECT 1 FROM messages preferred WHERE preferred.record_key=m.record_key AND preferred.record_source='omni-native' AND EXISTS(SELECT 1 FROM routes pr WHERE pr.message_id=preferred.id AND pr.tab IN ("+placeholders+"))))";java.util.Collections.addAll(parameters,selection);
    }else{java.util.ArrayList<String> clauses=new java.util.ArrayList<>();for(String channel:selection)clauses.add(rule(channel,parameters));where="("+String.join(" OR ",clauses)+")";}
   }
   if(!"frames".equals(view))where+=" AND EXISTS(SELECT 1 FROM routes owned WHERE owned.message_id=m.id) AND (m.record_source<>'native' OR NOT EXISTS(SELECT 1 FROM messages preferred WHERE preferred.record_key=m.record_key AND preferred.record_source='omni-native' AND EXISTS(SELECT 1 FROM routes pr WHERE pr.message_id=preferred.id)))";
   if(!thread.isEmpty()){where+=" AND m.target=?";parameters.add(thread);}args=parameters.toArray(new String[0]);
  }
 }
 static final class Page {JSONArray messages=new JSONArray();int total,filtered,start,end;String earliest="";String latestTime="";long latestId;}
 Page page(String current,String view,String thread,int offset) throws JSONException {
  return selectedPage(current,view,thread,offset,null);
 }
 Page selectedPage(String current,String view,String thread,int offset,String[] selection) throws JSONException {
  Filter f=new Filter(current,view,thread,selection);
  if(offset>0&&!anchorTime.isEmpty()){java.util.ArrayList<String> args=new java.util.ArrayList<>(java.util.Arrays.asList(f.args));f.where+=" AND (m.time<? OR (m.time=? AND m.id<=?))";args.add(anchorTime);args.add(anchorTime);args.add(String.valueOf(anchorId));f.args=args.toArray(new String[0]);}
  Page page=new Page();page.total=count();
  try(Cursor c=db.rawQuery("SELECT count(*) FROM messages m WHERE "+f.where,f.args)){if(c.moveToFirst())page.filtered=c.getInt(0);}
  try(Cursor c=db.rawQuery("SELECT time FROM messages m WHERE "+f.where+" ORDER BY time,id LIMIT 1",f.args)){if(c.moveToFirst())page.earliest=c.getString(0);}
  int safeOffset=Math.min(Math.max(0,offset),Math.max(0,page.filtered-1));page.end=Math.max(0,page.filtered-safeOffset);page.start=Math.max(0,page.end-200);
  try(Cursor c=db.rawQuery("SELECT payload FROM messages m WHERE "+f.where+" ORDER BY time,id LIMIT 200 OFFSET "+page.start,f.args)){
   int remaining=page.end-page.start;while(remaining-->0&&c.moveToNext())page.messages.put(new JSONObject(c.getString(0)));
  }
  if(page.messages.length()>0){JSONObject last=page.messages.getJSONObject(page.messages.length()-1);page.latestTime=last.optString("time");page.latestId=last.optLong("id");}
  return page;
 }
}
