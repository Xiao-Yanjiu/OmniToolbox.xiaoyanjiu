// SPDX-License-Identifier: GPL-3.0-or-later
package cn.xiaoyanjiu.ff14chat;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.media.*;
import android.os.Build;
import android.view.*;
import java.io.*;
import java.nio.ByteBuffer;
import javax.net.ssl.HttpsURLConnection;

// Independent implementation using Android public APIs. Network packets are bounded; overflow
// waits for an IDR and flushes stale decoded pictures before resuming.
final class HardwareStreamPlayer implements TextureView.SurfaceTextureListener {
 interface Listener {void status(int request,int fps,String error);}
 final TextureView view;private final Context context;private final Listener listener;
 private volatile Run run;private String session="",transport="tcp",udpHost="";private int udpPort;private int width,height,fps,request;
 HardwareStreamPlayer(Context c,Listener l){context=c;listener=l;view=new TextureView(c);view.setSurfaceTextureListener(this);view.setOpaque(true);view.setVisibility(View.GONE);}
 void start(String s,int w,int h,int f,int r,String route,String host,int port){stop();session=s;transport=route;udpHost=host;udpPort=port;width=w&~1;height=h&~1;fps=f;request=r;view.setVisibility(View.VISIBLE);if(view.isAvailable())launch();}
 void stop(){session="";Run old=run;run=null;if(old!=null)old.close();view.setVisibility(View.GONE);}
 private void launch(){if(session.isEmpty()||run!=null)return;Run next=new Run(session,width,height,fps,request,transport,udpHost,udpPort,new Surface(view.getSurfaceTexture()));run=next;new Thread(next::decode,"H264-decode").start();}
 public void onSurfaceTextureAvailable(SurfaceTexture t,int w,int h){launch();}
 public void onSurfaceTextureSizeChanged(SurfaceTexture t,int w,int h){}
 public boolean onSurfaceTextureDestroyed(SurfaceTexture t){Run old=run;run=null;if(old!=null)old.close();if(!session.isEmpty())listener.status(request,-1,"");return true;}
 public void onSurfaceTextureUpdated(SurfaceTexture t){}
 private final class Run {
  final String id,route,host;final int port;volatile UdpVideoTransport udp;final int w,h,target,number;final Surface surface;final VideoPacketQueue packets=new VideoPacketQueue();volatile boolean live=true;volatile HttpsURLConnection connection;volatile String failure="";
  Run(String s,int width,int height,int f,int n,String transport,String udpHost,int udpPort,Surface sf){id=s;route=transport;host=udpHost;port=udpPort;w=width;h=height;target=f;number=n;surface=sf;}
  void close(){live=false;UdpVideoTransport datagrams=udp;if(datagrams!=null)datagrams.close();HttpsURLConnection c=connection;if(c!=null)new Thread(c::disconnect,"H264-close").start();}
  void report(int fps,String error){if(run==this&&live)listener.status(number,fps,error);}
  void network(){int retries=0;while(live){HttpsURLConnection current=null;try{if(route.equals("udp")){receiveUdp();return;}current=Transport.openVideo(context,id);connection=current;int response=current.getResponseCode();if(response!=200){if(response==400||response==401||response==403||response==409){failure="视频会话已失效，请重新开始串流";return;}throw new IOException("视频连接响应异常："+response);}if(!"application/x-phonechat-h264".equals(current.getContentType())){failure="视频响应类型无效";return;}DataInputStream input=new DataInputStream(current.getInputStream());while(live){int length=Integer.reverseBytes(input.readInt());if(length<4||length>4194304){failure="H.264 视频包大小无效";return;}byte[] packet=new byte[length];input.readFully(packet);packets.offer(packet);retries=0;}}catch(Exception e){if(!live)return;packets.clear();if(++retries>3){failure="视频重连失败："+(e.getMessage()==null?"连接已断开":e.getMessage());return;}report(-1,"");try{Thread.sleep(250L*retries);}catch(InterruptedException interrupted){Thread.currentThread().interrupt();return;}}finally{if(current!=null)current.disconnect();if(connection==current)connection=null;}}}
  void receiveUdp()throws Exception{org.json.JSONObject info=Transport.request(context,"/stream",new org.json.JSONObject().put("action","udp-session").put("session",id));String address=host.isEmpty()?new java.net.URL(context.getSharedPreferences("pair",0).getString("url","")).getHost():host;java.net.InetAddress ip=null;for(java.net.InetAddress value:java.net.InetAddress.getAllByName(address))if(value instanceof java.net.Inet4Address){ip=value;break;}if(ip==null)throw new IOException("UDP当前需要可访问的IPv4地址");int remote=port>0?port:info.getInt("port");if(remote<1||remote>65535)throw new IOException("UDP端口无效");byte[] secret=android.util.Base64.decode(info.getString("key"),android.util.Base64.DEFAULT);UdpVideoTransport next=new UdpVideoTransport(new java.net.InetSocketAddress(ip,remote),secret,new UdpVideoProtocol.Sink(){public void frame(byte[] b){if(live)packets.offer(b);}public void loss(){packets.clear();}});java.util.Arrays.fill(secret,(byte)0);udp=next;try{if(live)next.run();}finally{next.close();if(udp==next)udp=null;}}
  String decoder()throws Exception{for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.ALL_CODECS).getCodecInfos()){if(info.isEncoder())continue;String name=info.getName().toLowerCase(java.util.Locale.ROOT);if(Build.VERSION.SDK_INT>=29?!info.isHardwareAccelerated():name.startsWith("omx.google.")||name.startsWith("c2.android."))continue;for(String type:info.getSupportedTypes())if(type.equalsIgnoreCase("video/avc")){MediaFormat format=MediaFormat.createVideoFormat("video/avc",w,h);if(info.getCapabilitiesForType(type).isFormatSupported(format))return info.getName();}}throw new IOException("手机没有支持此分辨率的 H.264 硬件解码器");}
  void decode(){MediaCodec codec=null;Thread receiver=null;try{codec=MediaCodec.createByCodecName(decoder());MediaFormat format=MediaFormat.createVideoFormat("video/avc",w,h);format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,4194304);format.setInteger(MediaFormat.KEY_PRIORITY,0);format.setInteger(MediaFormat.KEY_FRAME_RATE,target);if(Build.VERSION.SDK_INT>=30)format.setInteger(MediaFormat.KEY_LOW_LATENCY,1);codec.configure(format,surface,null,0);codec.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);codec.start();receiver=new Thread(this::network,"H264-network");receiver.start();MediaCodec.BufferInfo info=new MediaCodec.BufferInfo();long sequence=0,clock=android.os.SystemClock.elapsedRealtime(),startup=clock;int frames=0;boolean first=true,submitted=false;VideoPacketQueue.Packet pending=null;
   while(live){if(!failure.isEmpty())throw new IOException(failure);if(pending==null){pending=packets.poll();if(pending!=null&&pending.reset&&submitted){codec.flush();submitted=false;}}if(pending!=null){int index=codec.dequeueInputBuffer(1000);if(index>=0){ByteBuffer input=codec.getInputBuffer(index);if(input==null||input.capacity()<pending.bytes.length)throw new IOException("解码器输入缓冲不足");input.clear();input.put(pending.bytes);codec.queueInputBuffer(index,0,pending.bytes.length,sequence++*1000000L/target,0);pending=null;submitted=true;}}
    int index=codec.dequeueOutputBuffer(info,1000);int newest=-1;while(index>=0){if(newest>=0)codec.releaseOutputBuffer(newest,false);newest=index;index=codec.dequeueOutputBuffer(info,0);}if(newest>=0){codec.releaseOutputBuffer(newest,System.nanoTime());frames++;if(first){first=false;report(0,"");}}
    long now=android.os.SystemClock.elapsedRealtime();if(first&&now-startup>15000)throw new IOException("硬件视频首帧超时，请检查电脑编码器状态");if(now-clock>=1000){report((int)(frames*1000L/(now-clock)),"");clock=now;frames=0;}
   }
  }catch(Exception e){report(0,e.getMessage()==null?"硬件视频解码失败":e.getMessage());}finally{close();if(codec!=null){try{codec.stop();}catch(Exception ignored){}codec.release();}surface.release();packets.clear();if(receiver!=null){try{receiver.join(1000);}catch(InterruptedException e){Thread.currentThread().interrupt();}}}}
 }
}
