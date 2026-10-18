package cn.xiaoyanjiu.ff14chat;
import java.io.*;import java.util.*;
public final class GlamourFramePacketRegression {
 static void check(boolean ok){if(!ok)throw new AssertionError();}
 static byte[] packet(byte[] data)throws Exception{ByteArrayOutputStream out=new ByteArrayOutputStream();new DataOutputStream(out).writeInt(Integer.reverseBytes(data.length));out.write(data);return out.toByteArray();}
 static void rejects(byte[] data)throws Exception{try{GlamourFramePacket.read(new DataInputStream(new ByteArrayInputStream(data)));throw new AssertionError("accepted malformed record");}catch(IOException expected){}}
 public static void main(String[]args)throws Exception{ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] expected="{\"serial\":1,\"image\":\"fixture\"}".getBytes("UTF-8");for(int i=0;i<60;i++)bytes.write(packet(expected));DataInputStream stream=new DataInputStream(new FilterInputStream(new ByteArrayInputStream(bytes.toByteArray())){public int read(byte[] b,int off,int len)throws IOException{return super.read(b,off,Math.min(len,3));}});for(int i=0;i<60;i++)check(Arrays.equals(expected,GlamourFramePacket.read(stream)));check(stream.read()==-1);rejects(new byte[]{0,0,0,0});rejects(new byte[]{1,0,0,0});rejects(new byte[]{1,0,16,0});rejects(new byte[]{-1,-1,-1,-1});rejects(new byte[]{8,0,0,0,1});rejects(new byte[]{1,2});System.out.println("60 consecutive fragmented preview packets and 6 malformed/truncated record checks passed.");}
}
