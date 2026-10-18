package cn.xiaoyanjiu.ff14chat;
/** The DOM extractor validates the detail structure; mobile route/query variants remain valid. */
final class RisingStonesGlamour {
 static boolean allowedSource(String value){try{java.net.URI u=new java.net.URI(value);return "https".equalsIgnoreCase(u.getScheme())&&"ff14risingstones.web.sdo.com".equalsIgnoreCase(u.getHost())&&u.getUserInfo()==null&&(u.getPort()==-1||u.getPort()==443);}catch(Exception e){return false;}}
}
