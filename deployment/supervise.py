"""Local process supervisor for the installed app. No shell or remote downloads."""
import argparse
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import signal
import subprocess
import sys
import threading
import time
from urllib.request import build_opener, ProxyHandler


def reachable(url):
    try:
        with build_opener(ProxyHandler({})).open(url, timeout=2) as response:
            return response.status == 200
    except Exception:
        return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', required=True)
    parser.add_argument('--ollama', default='ollama')
    parser.add_argument('--model', default=os.environ.get('REPORTS_CHAT_MODEL', 'qwen2.5:7b'))
    parser.add_argument('--port', type=int, default=5187)
    parser.add_argument('--bind', default='127.0.0.1')
    args = parser.parse_args()
    root = Path(args.root).resolve()
    logs = root / 'logs'
    logs.mkdir(parents=True, exist_ok=True)
    stop_file = root / '.stop'
    stop_file.unlink(missing_ok=True)
    logger = logging.getLogger('reports')
    logger.setLevel(logging.INFO)
    handler = RotatingFileHandler(logs / 'services.log', maxBytes=5_000_000, backupCount=3, encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(asctime)s %(message)s'))
    logger.addHandler(handler)
    stopped = threading.Event()
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: stopped.set())
    env = dict(os.environ)
    env.update(ASPNETCORE_URLS=f'http://{args.bind}:{args.port}', PYTHONUNBUFFERED='1',
               REPORTS_CHAT_MODEL=args.model, LocalAI__ChatModel=args.model)
    commands = {}
    # Reuse a user's existing Ollama; only processes created here are stopped.
    if not reachable('http://127.0.0.1:11434/api/tags'):
        commands['ollama'] = [args.ollama, 'serve']
        env['OLLAMA_HOST'] = '127.0.0.1:11434'
    if reachable('http://127.0.0.1:8766/health'):
        raise RuntimeError('Port 8766 is already in use. Stop the manually started graph before starting the installed app.')
    commands['graph'] = [sys.executable, str(root / 'graph/server.py'), '--model', args.model,
                         '--model-timeout-seconds', '0', '--planner-timeout-seconds', '0',
                         '--planner-trace-path', str(logs / 'planner-trace.jsonl')]
    executable = root / 'web' / ('LaserficheReports.Web.exe' if os.name == 'nt' else 'LaserficheReports.Web')
    commands['web'] = [str(executable)] if executable.exists() else ['dotnet', str(root / 'web/LaserficheReports.Web.dll')]
    children = {}
    retries = {name: 0 for name in commands}
    restart_at = {name: 0 for name in commands}
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    def drain(name, pipe):
        try:
            for line in iter(pipe.readline, ''):
                logger.info('[%s] %s', name, line.rstrip())
        finally:
            pipe.close()
    try:
        while not stopped.is_set():
            if stop_file.exists():
                stop_file.unlink(missing_ok=True)
                break
            for name, command in commands.items():
                process = children.get(name)
                if process and process.poll() is None:
                    continue
                if process:
                    logger.error('%s exited (%s)', name, process.returncode)
                    retries[name] += 1
                    restart_at[name] = time.monotonic() + min(60, 2 ** min(retries[name], 6))
                    del children[name]
                if time.monotonic() < restart_at[name]:
                    continue
                try:
                    process = subprocess.Popen(command, cwd=root / 'web' if name == 'web' else root,
                        env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                        encoding='utf-8', errors='replace', creationflags=flags)
                    children[name] = process
                    threading.Thread(target=drain, args=(name, process.stdout), daemon=True).start()
                    logger.info('Started %s pid=%s', name, process.pid)
                except OSError as error:
                    logger.error('Cannot start %s: %s', name, error)
                    restart_at[name] = time.monotonic() + 30
            stopped.wait(1)
    finally:
        for process in children.values():
            if process.poll() is None:
                process.terminate()
        for process in children.values():
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()


if __name__ == '__main__':
    main()
