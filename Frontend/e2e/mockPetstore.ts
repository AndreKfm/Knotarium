import http from 'http';

let server: http.Server | null = null;
export interface MockRequest {
  method: string;
  url: string;
  headers: http.IncomingHttpHeaders;
  body?: string;
}

export const lastRequests: MockRequest[] = [];

export function startMockPetstore(port = 3456): Promise<void> {
  return new Promise((resolve, reject) => {
    lastRequests.length = 0;
    server = http.createServer((req, res) => {
      let body = '';
      req.on('data', chunk => {
        body += chunk;
      });
      req.on('end', () => {
        lastRequests.push({
          method: req.method || '',
          url: req.url || '',
          headers: req.headers,
          body: body || undefined,
        });

        // Set CORS headers
        res.setHeader('Content-Type', 'application/json');
        res.setHeader('Access-Control-Allow-Origin', '*');
        res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
        res.setHeader('Access-Control-Allow-Headers', '*');

        if (req.method === 'OPTIONS') {
          res.writeHead(200);
          res.end();
          return;
        }

        // Paths mirror the committed Petstore fixtures (/pets, /pets/{petId}).
        // GET /pets/{petId} -> 200 { id, name: "Buddy" }  (showPetById)
        const petByIdMatch = req.url?.match(/^\/pets\/([^/?]+)$/);
        if (req.method === 'GET' && petByIdMatch) {
          res.writeHead(200);
          res.end(JSON.stringify({ id: petByIdMatch[1], name: 'Buddy' }));
          return;
        }

        // POST /pets -> 201 { id: 123, name: "New Pet" }  (createPet)
        if (req.method === 'POST' && req.url?.startsWith('/pets')) {
          res.writeHead(201);
          res.end(JSON.stringify({ id: 123, name: 'New Pet' }));
          return;
        }

        // GET /pets -> 200 [{ id: 1, name: "Buddy" }]  (listPets)
        if (req.method === 'GET' && req.url?.startsWith('/pets')) {
          res.writeHead(200);
          res.end(JSON.stringify([{ id: 1, name: 'Buddy' }]));
          return;
        }

        res.writeHead(404);
        res.end(JSON.stringify({ error: 'Not Found' }));
      });
    });

    server.listen(port, () => {
      console.log(`Mock Petstore Server listening on port ${port}`);
      resolve();
    });

    server.on('error', (err) => {
      reject(err);
    });
  });
}

export function stopMockPetstore(): Promise<void> {
  return new Promise((resolve) => {
    if (server) {
      server.close(() => {
        server = null;
        resolve();
      });
    } else {
      resolve();
    }
  });
}
