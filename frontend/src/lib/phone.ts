// Egyptian mobile: 01 + carrier digit (0/1/2/5) + 8 digits = 11 digits total.
const EGYPT_MOBILE_RE = /^01[0125]\d{8}$/;

// Egyptian landline (أرضي): a leading 0, an area code, then the subscriber
// number — 8 to 10 digits all told. Cairo and Alexandria are one-digit codes
// (02, 03), everywhere else is two (057 دمياط, 040 طنطا, 050 المنصورة …), and
// subscriber numbers run 6 to 8 digits depending on the exchange.
//
// Deliberately structural rather than a list of area codes: the codes change,
// and a shop that can't save a real customer's number because the app's list
// is a year out of date is a worse failure than accepting a typo. The one
// thing it must NOT do is swallow mobiles — hence [2-9] in second position,
// since every mobile is 01x.
const EGYPT_LANDLINE_RE = /^0[2-9]\d{6,8}$/;

/**
 * People write phone numbers the way they'd read them aloud: with dashes,
 * spaces, or the country code in front (٠٥٧-٢٤٠١١٨٠, +20 100 123 4567). None
 * of that changes the number, so none of it should be grounds for rejection —
 * we compare the digits and let the user keep their own formatting.
 */
function toDigits(phone: string): string {
  const bare = phone.replace(/[\s\-()]/g, '');
  // +201… / 00201… / 201… all mean a local 01…
  return bare.replace(/^(?:\+20|0020|20)(?=[1-9])/, '0');
}

/** Egyptian mobile only — for InstaPay, which is addressed by mobile number. */
export function isValidEgyptianMobile(phone: string): boolean {
  return EGYPT_MOBILE_RE.test(toDigits(phone.trim()));
}

/** Any Egyptian phone a contact might give you: mobile or landline. */
export function isValidEgyptianPhone(phone: string): boolean {
  const digits = toDigits(phone.trim());
  return EGYPT_MOBILE_RE.test(digits) || EGYPT_LANDLINE_RE.test(digits);
}
