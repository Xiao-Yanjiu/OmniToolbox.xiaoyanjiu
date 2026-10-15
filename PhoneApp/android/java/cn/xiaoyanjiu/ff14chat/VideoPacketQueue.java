// SPDX-License-Identifier: GPL-3.0-or-later
package cn.xiaoyanjiu.ff14chat;
import java.util.ArrayDeque;
final class VideoPacketQueue {
 static final class Packet {final byte[] bytes;final boolean reset;Packet(byte[] b,boolean r){bytes=b;reset=r;}}
 private final ArrayDeque<Packet> queue=new ArrayDeque<>();private boolean needKey=true;
 static boolean key(byte[] b){for(int i=0;i+3<b.length;i++)if(b[i]==0&&b[i+1]==0&&b[i+2]==1&&(b[i+3]&31)==5)return true;return false;}
 synchronized void offer(byte[] bytes){if(queue.size()>=3){queue.clear();needKey=true;}if(needKey&&!key(bytes))return;queue.addLast(new Packet(bytes,needKey));needKey=false;}
 synchronized Packet poll(){return queue.pollFirst();}
 synchronized void clear(){queue.clear();needKey=true;}
 synchronized int size(){return queue.size();}
}
