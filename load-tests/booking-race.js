import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

export const createdCounter = new Counter('created_bookings');
export const conflictCounter = new Counter('conflict_bookings');
export const otherCounter = new Counter('other_responses');

export const options = {
  scenarios: {
    race_condition_test: {
      executor: 'per-vu-iterations',
      vus: 50,
      iterations: 1,
      maxDuration: '30s',
    },
  },
  thresholds: {
    // 409 Conflict is expected behavior for race losers!
    'http_req_failed': ['rate<0.05'], // Only non-409/non-201 errors fail
  },
};

const BASE_URL = __ENV.GATEWAY_URL || 'http://localhost:5104';
const ROOM_ID = parseInt(__ENV.ROOM_ID || '1');
const CHECK_IN = __ENV.CHECK_IN || '2027-03-10T14:00:00Z';
const CHECK_OUT = __ENV.CHECK_OUT || '2027-03-12T10:00:00Z';

export default function () {
  const payload = JSON.stringify({
    roomId: ROOM_ID,
    guestName: `Race Runner ${__VU}`,
    checkIn: CHECK_IN,
    checkOut: CHECK_OUT,
  });

  const params = {
    headers: {
      'Content-Type': 'application/json',
    },
  };

  const res = http.post(`${BASE_URL}/api/bookings`, payload, params);

  if (res.status === 201) {
    createdCounter.add(1);
  } else if (res.status === 409) {
    conflictCounter.add(1);
  } else {
    otherCounter.add(1);
    console.warn(`VU ${__VU} got unexpected status ${res.status}: ${res.body}`);
  }

  check(res, {
    'status is 201 or 409': (r) => r.status === 201 || r.status === 409,
  });
}
