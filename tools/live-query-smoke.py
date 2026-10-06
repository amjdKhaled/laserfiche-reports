"""Runtime contract tests with isolated fake LF and Ollama HTTP servers.

Build first: dotnet build LaserficheReports.sln
Run: python tools/live-query-smoke.py --dotnet dotnet
No real repository, documents, credentials or database are used.
"""
import argparse
import concurrent.futures
import http.cookiejar
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit
from urllib.request import Request, build_opener, HTTPCookieProcessor, ProxyHandler
from urllib.error import HTTPError

ROOT = Path(__file__).resolve().parents[1]
FIELDS = ['إجراء الوثيقة', 'الإدارة']
class Fixture(BaseHTTPRequestHandler):
    rows = [{ 'id': 608+i, 'name': f'وثيقة {i}', 'fullPath': f'\\الموارد البشرية\\وثيقة {i}', 'entryType': 'Document',
        'parentId': 10, 'templateName': 'الموظفين', 'templateId': 1,
        'creationTime': '2026-10-05T06:00:00Z', 'lastModifiedTime': f'2026-10-05T06:{i%60:02d}:00Z',
        'fields': [{'id': 1, 'name': FIELDS[0], 'values': ['تحت الإجراء' if i % 2 else 'مكتمل']},
                   {'id': 2, 'name': FIELDS[1], 'values': ['موارد بشرية' if i % 2 else 'تقنية المعلومات']}] } for i in range(125)]
    folders = [{'id':10,'name':'الموارد البشرية','fullPath':'\\الموارد البشرية','entryType':'Folder'}]
    searches = {}
    calls = []
    slow_search = False
    bad_lf = False
    ai_down = False
    def log_message(self, *_): pass
    def reply(self, value, status=200):
        data = json.dumps(value,ensure_ascii=False).encode()
        self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();
        try:self.wfile.write(data)
        except (BrokenPipeError,ConnectionResetError):pass
    def do_POST(self):
        if self.headers.get('Transfer-Encoding','').lower()=='chunked':
            chunks=[]
            while True:
                size=int(self.rfile.readline().split(b';')[0].strip(),16)
                if size==0:self.rfile.readline();break
                chunks.append(self.rfile.read(size));self.rfile.read(2)
            data=b''.join(chunks)
        else:data = self.rfile.read(int(self.headers.get('Content-Length',0)))
        if self.path == '/api/show':
            return self.reply({'capabilities':['completion']})
        if self.path.endswith('/Token'):
            if b'wrong' in data: return self.reply({},401)
            return self.reply({'access_token':'fixture-token','expires_in':3600})
        if self.path.endswith('/SearchAsync'):
            if self.bad_lf: return self.reply({},503)
            if self.slow_search: time.sleep(float(self.slow_search))
            expression=json.loads(data)['searchCommand'];self.calls.append(('search', expression))
            rows = self.folders if 'Type=F}' in expression else self.rows
            name=re.search(r'LF:Name="([^"]+)"',expression)
            if name and name[1]!='*': rows=[r for r in rows if r['name']==name[1]]
            for field,value in re.findall(r'\{\[\]:\[([^\]]+)\]="([^"]*)"\}',expression):
                rows=[r for r in rows if any(f['name']==field and value in f['values'] for f in r['fields'])]
            token=str(len(self.searches)+1);self.searches[token]=list(rows)
            return self.reply({'taskId':token,'status':'Completed'})
        if self.path == '/api/chat':
            if self.ai_down:return self.reply({},503)
            body=json.loads(data)
            if body.get('stream'):
                chunks=[{'message':{'content':'ملخص مبني على الأعداد الموثقة أعلاه.'},'done':False},{'message':{'content':''},'done':True}]
                payload=('\n'.join(json.dumps(x) for x in chunks)+'\n').encode()
                self.send_response(200);self.send_header('Content-Type','application/x-ndjson');self.send_header('Content-Length',str(len(payload)));self.end_headers();self.wfile.write(payload);return
            question=body['messages'][-1]['content']
            if 'حالات الوثائق' in question: query={'intent':'report','groupBy':[FIELDS[0]]}
            elif 'رتب الإدارات' in question: query={'intent':'report','groupBy':[FIELDS[1]]}
            elif 'محتويات مجلد' in question: query={'intent':'folder','folderName':'الموارد البشرية'}
            elif 'تستخدم قالب' in question: query={'intent':'search','template':'الموظفين'}
            else:query={'intent':'unsupported'}
            return self.reply({'message':{'content':json.dumps(query,ensure_ascii=False)}})
        return self.reply({},404)
    def do_GET(self):
        uri=urlsplit(self.path);q=parse_qs(uri.query);path=uri.path
        self.calls.append(('get',path))
        if path=='/api/tags':return self.reply({'models':[{'name':'fixture-model'}]})
        if self.bad_lf:return self.reply({},503)
        if path.endswith('/Repositories'):return self.reply({'value':[{'id':'TestRepo','name':'TestRepo'}]})
        if path.endswith('/FieldDefinitions'):return self.reply({'value':[{'id':i+1,'name':f,'fieldType':'String','isMultiValue':False} for i,f in enumerate(FIELDS)]})
        if path.endswith('/TemplateDefinitions'):return self.reply({'value':[{'id':1,'name':'الموظفين'}]})
        if '/Searches/' in path and path.endswith('/Results'):
            token=path.split('/Searches/')[1].split('/')[0];rows=self.searches[token]
            order=q.get('$orderby',[''])[0].split(' ')
            if len(order)==2:rows=sorted(rows,key=lambda r:r.get(order[0],''),reverse=order[1]=='desc')
            start=int(q.get('$skip',['0'])[0]);size=int(q.get('$top',['20'])[0])
            return self.reply({'value':rows[start:start+size],'@odata.count':len(rows)})
        if path.endswith('/Folder/Children'):
            start=int(q.get('$skip',['0'])[0]);size=int(q.get('$top',['20'])[0]);return self.reply({'value':self.rows[start:start+size],'@odata.count':len(self.rows)})
        match=re.search(r'/Entries/(\d+)(/fields)?$',path,re.I)
        if match:
            row=next((r for r in self.rows+self.folders if r['id']==int(match[1])),None)
            if row is None:return self.reply({},404)
            return self.reply({'value':row.get('fields',[])} if match[2] else row)
        return self.reply({},404)

def main():
    if hasattr(sys.stdout, 'reconfigure'): sys.stdout.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser();parser.add_argument('--dotnet',default='dotnet');args=parser.parse_args()
    server=ThreadingHTTPServer(('127.0.0.1',0),Fixture);threading.Thread(target=server.serve_forever,daemon=True).start()
    lf=f'http://127.0.0.1:{server.server_port}'
    # An adjacent ephemeral port can already belong to another Windows service.
    # Ask the OS for a separate available port instead of assuming port + 1 is free.
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        appport = reservation.getsockname()[1]
    base=f'http://127.0.0.1:{appport}'
    env={**os.environ,'ASPNETCORE_URLS':base,'Laserfiche__ServerUrl':lf,'Laserfiche__ApiVersion':'v2',
         'Laserfiche__RepositoryId':'TestRepo','LocalAI__BaseUrl':lf,'LocalAI__ChatModel':'',
         'Reports__QueryTimeoutSeconds':'3','Reports__HealthTimeoutSeconds':'2',
         'Supabase__PostgresConnectionString':'Host=invalid;Database=unused;Username=unused;Password=unused'}
    opener=build_opener(ProxyHandler({}),HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def call(path, data=None):
        request=Request(base+path, data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json'})
        try:
            with opener.open(request,timeout=10) as response:return response.status,response.read().decode()
        except HTTPError as error:return error.code,error.read().decode()
    log=tempfile.TemporaryFile()
    proc=subprocess.Popen([args.dotnet,str(ROOT/'src/LaserficheReports.Web/bin/Debug/net8.0/LaserficheReports.Web.dll')],cwd=ROOT/'src/LaserficheReports.Web',env=env,stdout=log,stderr=log)
    measurements={}
    try:
        started=time.monotonic()
        for _ in range(100):
            try:
                status,html=call('/');assert status==200 and 'التقارير الذكية' in html;break
            except OSError:time.sleep(.05)
        else:raise AssertionError('Application did not start')
        measurements['startup_shell_ms']=round((time.monotonic()-started)*1000,2)
        assert not any('/Entries/' in p for kind,p in Fixture.calls if kind=='get'), 'Startup enumerated entries'
        assert call('/health')[0]==200
        assert call('/api/session/login',{'username':'fixture','password':'wrong','repositoryId':'TestRepo'})[0]==401
        assert call('/api/session/login',{'username':'fixture','password':'fixture','repositoryId':'TestRepo'})[0]==200
        questions=['كم عدد الوثائق الموجودة في المستودع؟','كم عدد المجلدات؟',
         'ما الوثائق التي إجراء الوثيقة فيها تحت الإجراء؟','ما الوثائق التي إجراء الوثيقة فيها مكتمل؟',
         'اعرض آخر 10 وثائق معدلة.','اعرض الوثائق المنشأة اليوم.','ما القوالب الموجودة؟',
         'ما الوثائق التي تستخدم قالب الموظفين؟','اعرض محتويات مجلد الموارد البشرية.',
         'اعطني Metadata للوثيقة ID 608.','اعطني تقريرًا عن حالات الوثائق.','رتب الإدارات حسب عدد الوثائق.']
        durations=[]
        for question in questions:
            begin=time.monotonic();status,body=call('/api/reports/chat',{'question':question});durations.append((time.monotonic()-begin)*1000)
            assert status==200,(question,status,body)
            result=json.loads(body);assert result.get('query'),(question,body)
            print('PASS question:',question)
        status,body=call('/api/reports/documents?page=2');page=json.loads(body)
        assert status==200 and len(page['items'])==50 and page['totalCount']==125 and page['hasMore']
        changed=Fixture.rows[0];changed['name']='اسم محدث';changed['fullPath']='\\مجلد جديد\\اسم محدث';changed['fields'][0]['values']=['حالة جديدة']
        status,body=call('/api/reports/chat',{'question':'اعطني Metadata للوثيقة ID 608.'})
        assert status==200 and 'اسم محدث' in body and 'حالة جديدة' in body and 'مجلد جديد' in body
        Fixture.rows.pop();assert json.loads(call('/api/reports/chat',{'question':questions[0]})[1])['pagination']['totalCount']==124
        Fixture.rows.append({**changed,'id':9000,'name':'وثيقة جديدة'});assert json.loads(call('/api/reports/chat',{'question':questions[0]})[1])['pagination']['totalCount']==125
        Fixture.slow_search=True
        with concurrent.futures.ThreadPoolExecutor() as pool:
            running=pool.submit(call,'/api/reports/chat',{'question':questions[0]});time.sleep(.1)
            begin=time.monotonic();assert call('/api/app/status')[0]==200
            measurements['status_during_slow_chat_ms']=round((time.monotonic()-begin)*1000,2)
            assert measurements['status_during_slow_chat_ms']<700
            assert running.result()[0]==200
        Fixture.slow_search=4
        assert call('/api/reports/chat',{'question':questions[0]})[0]==504
        Fixture.slow_search=False
        status,sse=call('/api/reports/chat/stream',{'question':questions[-1]})
        assert status==200 and 'event: status' in sse and 'event: result' in sse and 'event: delta' in sse
        assert call('/api/reports/chat',{'question':questions[0],'previousQuery':{'intent':'count','name':'*'}})[0]==400
        status, count_sse=call('/api/reports/chat/stream',{'question':questions[0]})
        assert status==200 and 'event: delta' in count_sse and 'إجابة الذكاء الاصطناعي' in json.loads(re.search(r'event: delta\ndata: (.+)', count_sse)[1])['text']
        status,unsupported=call('/api/reports/chat',{'question':'لخص أهم النقاط في الوثيقة 618 كتقرير'})
        assert status==200 and 'غير متاح' in json.loads(unsupported)['answer']
        Fixture.ai_down=True;assert call('/api/reports/chat',{'question':questions[0]})[0]==200
        assert call('/api/reports/chat',{'question':'سؤال يحتاج ذكاء اصطناعي'})[0]==503
        Fixture.ai_down=False;Fixture.bad_lf=True
        assert call('/')[0]==200 and call('/health')[0]==200
        assert call('/api/reports/chat',{'question':questions[0]})[0] in (502,503)
        measurements['query_p50_ms']=round(sorted(durations)[len(durations)//2],2);measurements['query_p95_ms']=round(max(durations),2)
        print('PASS runtime checks: paging, live field/rename/move/create/delete, streaming, validation, concurrency, AI-down, LF-down, no database')
        print(json.dumps(measurements,indent=2))
    finally:
        proc.terminate();proc.wait(timeout=10);server.shutdown()
        if proc.returncode not in (0,-15):
            log.seek(0);print(log.read().decode()[-4000:])
        log.close()

if __name__=='__main__':main()
