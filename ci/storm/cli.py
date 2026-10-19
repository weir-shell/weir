# cli.py PORT RUNDIR (storm.py's serve fixture client): fire requests /0../N at the server, 4 at a time, forever; touch RUNDIR/started on the first answer
import socket, sys, threading, os, time
port = int(sys.argv[1]); d = os.path.dirname(os.path.abspath(sys.argv[0]))
started = os.path.join(sys.argv[2], 'started')
n = [0]; lk = threading.Lock()
def one():
    while True:
        with lk: n[0] += 1; i = n[0]
        try:
            s = socket.create_connection(("127.0.0.1", port), timeout=10)
            s.sendall(b"GET /%d HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n" % i)
            while s.recv(65536): pass
            open(started, 'a').close()
        except Exception:
            time.sleep(0.02)
for _ in range(4): threading.Thread(target=one, daemon=True).start()
while True: time.sleep(1)
