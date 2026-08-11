import { createClient } from '@supabase/supabase-js';

/**
 * PC EDITION DIVERGENCE — the API base is the origin this page came from.
 *
 * supabase-js derives every endpoint (`/rest/v1`, `/auth/v1`) from one base
 * URL, and on this edition the same Caddy that served this page also proxies
 * those paths. So the correct base is always "wherever the browser just got
 * this page from".
 *
 * The cloud build bakes a fixed URL at build time. Doing that here is wrong in
 * three ways, and each one is a real machine:
 *
 *   - Over the public link, a baked `http://localhost:8000` makes the VISITOR'S
 *     browser call the VISITOR'S own localhost. The app loads and then every
 *     request fails.
 *   - Same for a phone or a second PC on the shop's wifi.
 *   - The HTTP port is chosen per machine (Windows reserves ranges), so even on
 *     the shop PC a hardcoded port can be the wrong one after a reinstall.
 *
 * `VITE_SUPABASE_URL` is still read, and still carries the install-time token
 * that patch-frontend-config.ps1 replaces, so the build guard that checks the
 * token reached the bundle keeps working. In a browser it is never the value
 * used — it is the fallback for a non-DOM context, which this app does not
 * have. See supabase/PC-DIVERGENCE.md.
 */
const bakedUrl = import.meta.env.VITE_SUPABASE_URL;
const supabaseUrl =
  typeof window !== 'undefined' && window.location?.origin ? window.location.origin : bakedUrl;

const supabaseAnonKey = import.meta.env.VITE_SUPABASE_ANON_KEY;

export const supabase = createClient(supabaseUrl, supabaseAnonKey);
