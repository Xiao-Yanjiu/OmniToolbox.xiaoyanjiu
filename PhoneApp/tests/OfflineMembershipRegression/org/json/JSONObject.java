package org.json; import java.util.*; public class JSONObject {
final Map<String,Object> values=new HashMap<>();public JSONObject(){}public JSONObject(String s) throws JSONException{}
public JSONObject put(String k,Object v)throws JSONException{values.put(k,v);return this;}
public String optString(String k){return optString(k,"");}public String optString(String k,String d){Object v=values.get(k);return v==null?d:String.valueOf(v);}
public int optInt(String k,int d){Object v=values.get(k);return v instanceof Number?((Number)v).intValue():d;}
public long optLong(String k,long d){Object v=values.get(k);return v instanceof Number?((Number)v).longValue():d;}
public int getInt(String k){return optInt(k,0);}public boolean has(String k){return values.containsKey(k);}
public JSONObject optJSONObject(String k){Object v=values.get(k);return v instanceof JSONObject?(JSONObject)v:null;}
public String toString(){return values.toString();}
}