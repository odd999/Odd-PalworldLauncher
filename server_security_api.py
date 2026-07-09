import os
import sys
import time
import hmac
import hashlib
import threading
import socket
import struct
from http.server import HTTPServer, BaseHTTPRequestHandler

# CONFIGURATION
PORT = 8000
API_SECRET = "MySuperPrivateLauncherKey2026!25"  # MUST MATCH the ApiSecret in the launcher's manifest.json

# RCON Settings
RCON_HOST = "192.168.50.111"
RCON_PORT = 33102
RCON_PASSWORD = "OWccFZIe0dKj"     # Configure this to match the AdminPassword in your PalWorldSettings.ini

# Shared Whitelist State
active_leases = {}
leases_lock = threading.Lock()

class RconClient:
    def __init__(self, host, port, password):
        self.host = host
        self.port = port
        self.password = password
        self.sock = None

    def connect(self):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.sock.settimeout(5.0)
        self.sock.connect((self.host, self.port))
        
        # Authenticate (Source RCON type 3)
        self._send_packet(1, 3, self.password)
        resp_id, resp_type, _ = self._recv_packet()
        if resp_id == -1:
            raise Exception("RCON Authentication Failed: Incorrect Password.")

    def send_command(self, cmd):
        if not self.sock:
            self.connect()
        self._send_packet(2, 2, cmd)
        resp_id, resp_type, payload = self._recv_packet()
        return payload

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except:
                pass
            self.sock = None

    def _send_packet(self, req_id, req_type, payload):
        encoded_payload = payload.encode('utf-8')
        # Size = 4 (id) + 4 (type) + len(payload) + 1 (null terminator) + 1 (null pad)
        size = 4 + 4 + len(encoded_payload) + 2
        packet = struct.pack(f"<iii{len(encoded_payload)}sBB", size, req_id, req_type, encoded_payload, 0, 0)
        self.sock.sendall(packet)

    def _recv_packet(self):
        size_data = self.sock.recv(4)
        if not size_data or len(size_data) < 4:
            raise Exception("Connection closed by remote host.")
        size = struct.unpack("<i", size_data)[0]
        
        packet_data = b""
        while len(packet_data) < size:
            chunk = self.sock.recv(size - len(packet_data))
            if not chunk:
                raise Exception("Connection truncated.")
            packet_data += chunk
            
        req_id, req_type = struct.unpack("<ii", packet_data[:8])
        payload = packet_data[8:-2].decode('utf-8', errors='ignore')
        return req_id, req_type, payload

def update_lease(steam_id):
    global active_leases
    current_time = int(time.time())
    new_expiry = current_time + 20  # 20-second whitelist lease
    
    with leases_lock:
        if steam_id not in active_leases:
            print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Whitelisting Steam ID: {steam_id}")
        else:
            print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Renewing lease for Steam ID: {steam_id} (+20 seconds)")
        
        active_leases[steam_id] = new_expiry

def lease_cleaner_daemon():
    while True:
        try:
            time.sleep(5)
            current_time = int(time.time())
            expired_ids = []
            
            with leases_lock:
                for steam_id, expiry in list(active_leases.items()):
                    if current_time > expiry:
                        expired_ids.append(steam_id)
                        del active_leases[steam_id]
            
            for steam_id in expired_ids:
                print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Lease expired. Steam ID removed from whitelist: {steam_id}")
        except Exception as e:
            print(f"ERROR in lease cleaner daemon: {e}")

class SecurityRequestHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        # Suppress standard HTTP logs in output
        pass

    def do_POST(self):
        if self.path != "/allow-connection":
            self.send_response(404)
            self.end_headers()
            self.wfile.write(b"Not Found")
            return
        
        timestamp = self.headers.get("X-Timestamp")
        signature = self.headers.get("X-Signature")
        steam_id = self.headers.get("X-Steam-ID")
        
        if not timestamp or not signature or not steam_id:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"Missing security headers.")
            return
        
        # Verify timestamp is within 60 seconds to prevent replay attacks
        try:
            req_time = int(timestamp)
            current_time = int(time.time())
            if abs(current_time - req_time) > 60:
                self.send_response(403)
                self.end_headers()
                self.wfile.write(b"Request expired (timestamp out of sync).")
                return
        except ValueError:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"Invalid timestamp format.")
            return

        # Compute and compare HMAC signature over "timestamp:steam_id"
        key = API_SECRET.encode('utf-8')
        message = f"{timestamp}:{steam_id}".encode('utf-8')
        expected_sig = hmac.new(key, message, hashlib.sha256).hexdigest()

        if not hmac.compare_digest(expected_sig, signature):
            self.send_response(403)
            self.end_headers()
            self.wfile.write(b"Invalid signature.")
            return

        # Update lease for Steam ID
        update_lease(steam_id)

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"status": "success", "message": "Steam ID whitelisted"}')

def parse_players(players_text):
    lines = players_text.strip().split('\n')
    players = []
    for line in lines:
        parts = [p.strip() for p in line.strip().split(',')]
        if len(parts) >= 3:
            name, playeruid, steamid = parts[0], parts[1], parts[2]
            # Ignore CSV header line
            if steamid.lower() == "steamid" or not steamid.isdigit():
                continue
            players.append((name, playeruid, steamid))
    return players

def rcon_watchdog_daemon():
    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Starting RCON Watchdog Daemon...")
    rcon = RconClient(RCON_HOST, RCON_PORT, RCON_PASSWORD)
    
    while True:
        try:
            # Query server for players
            players_text = rcon.send_command("ShowPlayers")
            online_players = parse_players(players_text)
            
            # Fetch currently whitelisted Steam IDs
            current_time = int(time.time())
            valid_steam_ids = set()
            with leases_lock:
                for steam_id, expiry in list(active_leases.items()):
                    if current_time <= expiry:
                        valid_steam_ids.add(steam_id)
            
            # Check online players
            for name, playeruid, steamid in online_players:
                if steamid not in valid_steam_ids:
                    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Kicking unauthorized player: {name} (Steam ID: {steamid}, UID: {playeruid})")
                    res_prefix = rcon.send_command(f"KickPlayer steam_{steamid}")
                    res_steam = rcon.send_command(f"KickPlayer {steamid}")
                    res_uid = rcon.send_command(f"KickPlayer {playeruid}")
                    print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] Kick responses -> Steam_Prefix: '{res_prefix}' | SteamID: '{res_steam}' | UID: '{res_uid}'")
                    
        except Exception as e:
            # If server connection fails or RCON fails, close connection to retry
            rcon.close()
            print(f"RCON Watchdog warning: {e}")
            
        time.sleep(2)  # Check every 2 seconds

def run_server():
    # Start cleaner daemon
    threading.Thread(target=lease_cleaner_daemon, daemon=True).start()
    
    # Start RCON watchdog daemon
    threading.Thread(target=rcon_watchdog_daemon, daemon=True).start()

    server_address = ('', PORT)
    httpd = HTTPServer(server_address, SecurityRequestHandler)
    print(f"Palworld Security HTTP API running on port {PORT}...")
    print(f"RCON Watchdog monitoring Server {RCON_HOST}:{RCON_PORT} (kicks non-whitelisted players)")
    print("NOTE: No Administrator/root privileges are required to run this script.")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down server...")
        httpd.server_close()

if __name__ == '__main__':
    run_server()
