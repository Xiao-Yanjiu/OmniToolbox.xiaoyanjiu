package cn.xiaoyanjiu.ff14chat;
import java.io.*;
/** Bounded little-endian records shared with the server's dedicated preview stream. */
final class GlamourFramePacket {
 static byte[] read(DataInputStream input)throws IOException{int size=Integer.reverseBytes(input.readInt());if(size<2||size>1024*1024)throw new IOException("预览帧长度无效");byte[] bytes=new byte[size];input.readFully(bytes);return bytes;}
}
