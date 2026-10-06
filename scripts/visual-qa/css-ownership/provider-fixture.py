"""Loopback-only Apple API test responses for disposable CSS ownership captures."""
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        result = {'wrapperType': 'track', 'kind': 'ebook', 'trackId': 900001,
                  'trackName': 'The First Coast', 'artistName': 'Jamie Rivers',
                  'artistId': 900002, 'releaseDate': '2024-03-05T00:00:00Z',
                  'description': 'A synthetic identity candidate for layout verification.',
                  'primaryGenreName': 'Fiction', 'genres': ['Fiction'], 'language': 'EN',
                  'trackViewUrl': 'http://127.0.0.1:61499/lookup?id=900001'}
        data = json.dumps({'resultCount': 1, 'results': [result]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, format, *args):
        pass  # No query, credentials, or private fixture state in evidence logs.


if __name__ == '__main__':
    server = ThreadingHTTPServer(('127.0.0.1', 61499), Handler)
    print('Disposable provider fixture listening on loopback port 61499', flush=True)
    server.serve_forever()
