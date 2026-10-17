package cn.xiaoyanjiu.ff14chat;
/** Accept browser-valid HTTPS queries without treating OAuth state as strict URI syntax. */
final class RisingStonesNavigation {
 static String upgradeHttp(String value){try{if(value==null)return null;for(int i=0;i<value.length();i++)if(Character.isISOControl(value.charAt(i)))return null;java.net.URL u=new java.net.URL(value);if(!"http".equalsIgnoreCase(u.getProtocol())||u.getUserInfo()!=null||(u.getPort()!=-1&&u.getPort()!=80))return null;String target=new java.net.URL("https",u.getHost(),-1,u.getFile()+(u.getRef()==null?"":"#"+u.getRef())).toExternalForm();return allowed(target)?target:null;}catch(Exception e){return null;}}
 static boolean allowed(String value){try{if(value==null)return false;for(int i=0;i<value.length();i++)if(Character.isISOControl(value.charAt(i)))return false;java.net.URL u=new java.net.URL(value);String host=u.getHost();return "https".equalsIgnoreCase(u.getProtocol())&&host!=null&&!host.isEmpty()&&!host.contains(" ")&&u.getUserInfo()==null&&(u.getPort()==-1||u.getPort()==443);}catch(Exception e){return false;}}
}
