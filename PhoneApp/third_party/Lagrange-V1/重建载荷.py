# SPDX-License-Identifier: GPL-3.0-or-later
from pathlib import Path
import base64,gzip
out=Path('bin/Release/net9.0')
rows=[]
for name in ['Lagrange.Core','protobuf-net','protobuf-net.Core','PhoneQQBridge']:
    encoded=base64.b64encode(gzip.compress((out/(name+'.dll')).read_bytes(),mtime=0)).decode()
    chunks=[encoded[i:i+12000] for i in range(0,len(encoded),12000)]
    rows.append('["'+name+'"]=string.Concat(new[]{'+','.join('"'+c+'"' for c in chunks)+'})')
Path('QQ载荷.txt').write_text(',\n'.join(rows),encoding='utf-8')
