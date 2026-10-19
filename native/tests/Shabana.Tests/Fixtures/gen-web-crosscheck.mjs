// Generates native/tests/Shabana.Tests/Fixtures/web-crosscheck.json from the ACTUAL web code in Shabana-Inventory/src.
// Usage: node --experimental-strip-types gen.mjs <web-src-dir> <out.json>
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [webSrc, out] = process.argv.slice(2);
const work = fs.mkdtempSync(path.join(process.cwd(), 'w-'));
const copy = (rel, name = path.basename(rel)) => {
  let t = fs.readFileSync(path.join(webSrc, rel), 'utf8').replace(/@\/lib\/(\w+)'/g, "./$1.ts'");
  fs.writeFileSync(path.join(work, name), t);
};
['lib/search.ts', 'lib/phone.ts', 'lib/username.ts', 'lib/digits.ts', 'lib/date.ts', 'lib/locale.ts'].forEach((f) => copy(f));
const md = fs.readFileSync(path.join(webSrc, 'components/shared/MoneyDisplay.tsx'), 'utf8').replace(/@\/lib\/(\w+)'/g, "./$1.ts'");
const a = md.indexOf('export function formatMoney');
const b = md.indexOf('interface MoneyDisplayProps');
fs.writeFileSync(path.join(work, 'money.ts'), md.slice(0, md.indexOf('export function formatMoney')).split('\n').filter((l) => l.startsWith('import')).join('\n') + '\n' + md.slice(a, b));

const imp = (n) => import(pathToFileURL(path.join(work, n)).href);
const search = await imp('search.ts');
const phone = await imp('phone.ts');
const username = await imp('username.ts');
const digits = await imp('digits.ts');
const date = await imp('date.ts');
const money = await imp('money.ts');

// deterministic PRNG
let s = 123456789;
const rnd = () => ((s = (Math.imul(s, 1664525) + 1013904223) >>> 0) / 2 ** 32);
const pick = (arr) => arr[Math.floor(rnd() * arr.length)];

const vec = { search: [], phone: [], username: [], digits: [], money: [], qty: [], dateShort: [], dateLong: [] };

const arabicAlphabet = 'ابتثجحخدذرزسشصضطظعغفقكلمنهوي' + 'أإآٱةىئؤء' + 'ًٌٍَُِّْٰـ' + '٠١٢٣٤٥٦٧٨٩۰۱۲۳۴۵۶۷۸۹' + 'abcXYZ019 \t 　';
for (let i = 0; i < 4000; i++) {
  const n = 1 + Math.floor(rnd() * 14);
  let t = '';
  for (let j = 0; j < n; j++) t += pick([...arabicAlphabet]);
  vec.search.push({ input: t, normalized: search.normalize(t) });
}
for (const t of ['  أَحْمَد   عِيَاد ', 'شبانة', 'شبانه', 'على', 'علي', 'مؤسسة', 'مئة', 'ـــ', 'ABC Def', 'أمر بيع ١٢ · الامام عياد', '', ' ', 'ٱلله', 'إسماعيل', 'ﷲ'])
  vec.search.push({ input: t, normalized: search.normalize(t) });

const phones = ['01012345678', '+201012345678', '00201012345678', '201012345678', '010 1234 5678', '010-1234-5678', '(010)12345678', '01312345678', '0101234567', '0570-2401180'.replace('-', ''), '057-2401180', '0572401180', '02-23456789', '0223456789', '023456789', '0123456789012', '1012345678', '+20 100 123 4567', '', '   ', 'abc', '0112345678', '01512345678', '0201234567', '0801234567', '0901234567', '001012345678', '02123456', '021234567', '0212345678', '02123456789'];
for (const p of phones) vec.phone.push({ input: p, mobile: phone.isValidEgyptianMobile(p), phone: phone.isValidEgyptianPhone(p) });
for (let i = 0; i < 1500; i++) {
  const len = 6 + Math.floor(rnd() * 8);
  let t = pick(['', '+20', '0020', '20', '0']);
  for (let j = 0; j < len; j++) t += pick([...'0123456789 -()']);
  vec.phone.push({ input: t, mobile: phone.isValidEgyptianMobile(t), phone: phone.isValidEgyptianPhone(t) });
}

for (const u of ['ahmed', 'Ahmed', 'a.b_c-d', 'user name', 'a@b', 'ab1', '', 'أحمد', 'x'.repeat(70), 'ADMIN', 'a/b'])
  vec.username.push({ input: u, valid: username.isValidUsername(u) });

for (const d of ['١٢٣', '۱۲۳', '12٫5', '1,5', '١٬٢٣٤٫٥', '1 234', 'abc', '-5', '٠']) vec.digits.push({ input: d, latin: digits.toLatinDigits(d), typing: digits.normalizeNumericInput(d) });

const amounts = [0, 1, 0.5, 0.01, 0.1, 5, 12345.5, 500, 18.5, 19, 56, 1234567.89, 0.995, 1.005, 2.675, 99999999999.99, 100, 1000, 999.999, 0.004, 0.005];
for (let i = 0; i < 1500; i++) amounts.push(Math.round(rnd() * 1e9) / 100);
for (const a of amounts) vec.money.push({ piasters: Math.round(a * 100), web: money.formatMoney(a) });
const qtys = [0, 1, 2.5, 40, 0.001, 0.0005, 1.2345, 1000, 1234567.891, 0.1, 12.3];
for (let i = 0; i < 1500; i++) qtys.push(Math.round(rnd() * 1e9) / 1000);
for (const q of qtys) { vec.qty.push({ milli: Math.round(q * 1000), web: money.formatQty(q), webUnit: money.formatQty(q, 'KGU') }); }

for (let y of [2014, 2023, 2026, 1999, 2100]) for (let m = 1; m <= 12; m++) for (const d of [1, 5, 9, 28, 31].filter((d) => new Date(y, m - 1, d).getMonth() === m - 1)) {
  const iso = `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
  vec.dateShort.push({ iso, web: date.formatDateShort(iso) });
  vec.dateLong.push({ iso, web: date.formatDateLong(iso) });
}
fs.writeFileSync(out, JSON.stringify(vec, null, 0));
fs.rmSync(work, { recursive: true });
console.log(Object.fromEntries(Object.entries(vec).map(([k, v]) => [k, v.length])));
