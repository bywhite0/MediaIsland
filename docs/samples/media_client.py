"""MediaLink 客户端示例 — 显示当前播放信息（≤30 行）"""
import asyncio, json, sys, websockets

TOKEN = sys.argv[1] if len(sys.argv) > 1 else "YOUR_TOKEN"
URL   = sys.argv[2] if len(sys.argv) > 2 else "ws://127.0.0.1:21757/v1/ws"

async def main():
    async with websockets.connect(URL) as ws:
        await ws.recv()  # server.hello
        await ws.send(json.dumps({"type": "auth", "id": "a1", "v": 1, "ts": 0, "payload": {"token": TOKEN}}))
        print("←", (await ws.recv())[:40])  # auth_ok
        await ws.send(json.dumps({"type": "subscribe", "id": "s1", "v": 1, "ts": 0, "payload": {"channels": ["media", "lyrics"]}}))
        await ws.recv()  # subscribe_ok
        print("已连接，等待媒体事件...")
        async for raw in ws:
            msg = json.loads(raw)
            if msg.get("name") == "media.updated":
                p = msg["payload"]
                pos = p.get("positionMs", 0) // 1000
                dur = p.get("durationMs", 0) // 1000
                print(f"♫ {p.get('title', '?')} — {p.get('artist', '?')}  [{pos//60}:{pos%60:02d}/{dur//60}:{dur%60:02d}]  {p.get('playbackState', '')}")
            elif msg.get("name") == "lyrics.updated":
                doc = (msg.get("payload") or {}).get("document") or {}
                lines = doc.get("lines") or []
                if lines:
                    print(f"  📝 歌词: {len(lines)} 行 (来源: {msg['payload'].get('source', '?')})")

if __name__ == "__main__":
    asyncio.run(main())