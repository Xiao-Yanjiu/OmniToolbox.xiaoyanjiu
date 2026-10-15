// SPDX-License-Identifier: GPL-3.0-or-later
package cn.xiaoyanjiu.ff14chat;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.media.*;
import android.os.Build;
import android.view.*;
import java.io.*;
import java.nio.ByteBuffer;
import java.util.concurrent.ArrayBlockingQueue;
import javax.net.ssl.HttpsURLConnection;

// Independent implementation using Android public APIs. Network packets are bounded; overflow
// reconnects at an IDR instead of feeding a decoder corrupted dependent frames.
final class HardwareStreamPlayer implements TextureView.SurfaceTextureListener {
 interface Listener {void status(int request,int fps,String error);}
 final TextureView view;private final Context context;private final Listener listener;
 private volatile Run run;private String session="";private int width,height,fps,request;
 HardwareStreamPlayer(Context c,Listener l){context=c;listener=l;view=new TextureView(c);view.setSurfaceTextureListener(this);view.setOpaque(true);view.setVisibility(View.GONE);}
 void start(String s,int w,int h,int f,int r){stop();session=s;width=w&~1;height=h&~1;fps=f;request=r;view.setVisibility(View.VISIBLE);if(view.isAvailable())launch();}
 void stop(){session="";Run old=run;run=null;if(old!=null)old.close();view.setVisibility(View.GONE);}
 private void launch(){if(session.isEmpty()||run!=null)return;Run next=new Run(session,width,height,fps,request,new Surface(view.getSurfaceTexture()));run=next;new Thread(next::decode,"H264-decode").start();}
 public void onSurfaceTextureAvailable(SurfaceTexture t,int w,int h){launch();}
 public void onSurfaceTextureSizeChanged(SurfaceTexture t,int w,int h){}
 public boolean onSurfaceTextureDestroyed(SurfaceTexture t){stop();return true;}
 public void onSurfaceTextureUpdated(SurfaceTexture t){}
 private final class Run {
  final String id;final int w,h,target,number;final Surface surface;final ArrayBlockingQueue<byte[]> packets=new ArrayBlockingQueue<>(3);volatile boolean live=true;volatile HttpsURLConnection connection;volatile String failure="";
  Run(String s,int width,int height,int f,int n,Surface sf){id=s;w=width;h=height;target=f;number=n;surface=sf;}
  void close(){live=false;HttpsURLConnection c=connection;if(c!=null)new Thread(c::disconnect,"H264-close").start();}
  void report(int fps,String error){if(run==this&&live)listener.status(number,fps,error);}
  void network(){try{connection=Transport.openVideo(context,id);if(connection.getResponseCode()!=200)throw new IOException("视频会话已失效，请重新开始串流");if(!"application/x-phonechat-h264".equals(connection.getContentType()))throw new IOException("视频响应类型无效");DataInputStream input=new DataInputStream(connection.getInputStream());while(live){int length=Integer.reverseBytes(input.readInt());if(length<4||length>4194304)throw new IOException("H.264 视频包大小无效");byte[] packet=new byte[length];input.readFully(packet);if(!packets.offer(packet))throw new IOException("视频解码队列已满，请降低帧率或码率后重新开始");}}catch(Exception e){if(live)failure=e.getMessage()==null?"视频连接已断开":e.getMessage();}finally{HttpsURLConnection c=connection;if(c!=null)c.disconnect();}}
  String decoder()throws Exception{for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.ALL_CODECS).getCodecInfos()){if(info.isEncoder())continue;String name=info.getName().toLowerCase(java.util.Locale.ROOT);if(Build.VERSION.SDK_INT>=29?!info.isHardwareAccelerated():name.startsWith("omx.google.")||name.startsWith("c2.android."))continue;for(String type:info.getSupportedTypes())if(type.equalsIgnoreCase("video/avc")){MediaFormat format=MediaFormat.createVideoFormat("video/avc",w,h);if(info.getCapabilitiesForType(type).isFormatSupported(format))return info.getName();}}throw new IOException("手机没有支持此分辨率的 H.264 硬件解码器");}
  void decode(){MediaCodec codec=null;Thread receiver=null;try{codec=MediaCodec.createByCodecName(decoder());MediaFormat format=MediaFormat.createVideoFormat("video/avc",w,h);format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE,4194304);format.setInteger(MediaFormat.KEY_PRIORITY,0);format.setInteger(MediaFormat.KEY_FRAME_RATE,target);if(Build.VERSION.SDK_INT>=30)format.setInteger(MediaFormat.KEY_LOW_LATENCY,1);codec.configure(format,surface,null,0);codec.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);codec.start();receiver=new Thread(this::network,"H264-network");receiver.start();MediaCodec.BufferInfo info=new MediaCodec.BufferInfo();long sequence=0,clock=android.os.SystemClock.elapsedRealtime(),startup=clock;int frames=0;boolean first=true;
   while(live){if(!failure.isEmpty())throw new IOException(failure);byte[] pending=packets.peek();if(pending!=null){int index=codec.dequeueInputBuffer(1000);if(index>=0){ByteBuffer input=codec.getInputBuffer(index);if(input==null||input.capacity()<pending.length)throw new IOException("解码器输入缓冲不足");input.clear();input.put(pending);packets.poll();codec.queueInputBuffer(index,0,pending.length,sequence++*1000000L/target,0);}}
    int index=codec.dequeueOutputBuffer(info,1000);int newest=-1;while(index>=0){if(newest>=0)codec.releaseOutputBuffer(newest,false);newest=index;index=codec.dequeueOutputBuffer(info,0);}if(newest>=0){codec.releaseOutputBuffer(newest,true);frames++;if(first){first=false;report(0,"");}}
    long now=android.os.SystemClock.elapsedRealtime();if(first&&now-startup>15000)throw new IOException("硬件视频首帧超时，请检查电脑编码器状态");if(now-clock>=1000){report((int)(frames*1000L/(now-clock)),"");clock=now;frames=0;}
   }
  }catch(Exception e){report(0,e.getMessage()==null?"硬件视频解码失败":e.getMessage());}finally{close();if(codec!=null){try{codec.stop();}catch(Exception ignored){}codec.release();}surface.release();packets.clear();if(receiver!=null){try{receiver.join(1000);}catch(InterruptedException e){Thread.currentThread().interrupt();}}}}
 }
}
