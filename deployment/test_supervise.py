"""Process lifecycle smoke test; no real services, credentials or models."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

@unittest.skipIf(os.name == 'nt', 'POSIX process fixture; Windows task acceptance is separate')
class SupervisorTests(unittest.TestCase):
    def test_stop_marker_terminates_only_owned_children(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'graph').mkdir(); (root / 'web').mkdir()
            code = ('#!' + sys.executable + '\nimport signal,time,pathlib,sys\n'
                    + 'root=pathlib.Path(' + repr(folder) + ')\n'
                    + 'name=pathlib.Path(sys.argv[0]).name\n'
                    + 'def stop(*args):\n (root/(name+".stopped")).touch();sys.exit(0)\n'
                    + 'signal.signal(signal.SIGTERM,stop)\n'
                    + '(root/(name+".started")).touch()\nwhile True: time.sleep(.1)\n')
            for path in (root/'graph/server.py',root/'web/LaserficheReports.Web',root/'ollama'):
                path.write_text(code);path.chmod(0o755)
            process = subprocess.Popen([sys.executable, str(Path(__file__).with_name('supervise.py')),
                '--root',folder,'--ollama',str(root/'ollama')])
            try:
                deadline=time.monotonic()+12
                while len(list(root.glob('*.started')))<3 and time.monotonic()<deadline:
                    time.sleep(.1)
                self.assertEqual(len(list(root.glob('*.started'))),3)
                (root/'.stop').touch()
                self.assertEqual(process.wait(timeout=15),0)
                self.assertEqual(len(list(root.glob('*.stopped'))),3)
            finally:
                if process.poll() is None:
                    process.terminate();process.wait(timeout=15)

if __name__=='__main__':unittest.main()
