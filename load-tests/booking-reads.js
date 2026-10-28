import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '10s', target: 50 },
    { duration: '20s', target: 200 },
    { duration: '10s', target: 0 },
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],   // <1% errors
    http_req_duration: ['p(95)<1000'], // 95% under 1000ms
  },
};

const BASE_URL = __ENV.GATEWAY_URL || 'http://localhost:5104';

export default function () {
  // Read all properties (cached via Redis in CatalogService)
  const res1 = http.get(`${BASE_URL}/api/catalog/properties`);
  check(res1, {
    'properties status is 200': (r) => r.status === 200,
  });

  // Read rooms for property 1
  const res2 = http.get(`${BASE_URL}/api/catalog/properties/1/rooms`);
  check(res2, {
    'rooms status is 200': (r) => r.status === 200,
  });

  sleep(0.1);
}
