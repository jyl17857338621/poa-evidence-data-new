const crypto = require('crypto');
function s(pw, salt, N, r, p, dk){
  return crypto.scryptSync(pw, salt, dk, {N,r,p}).toString('hex');
}
console.log("rfc empty N16 r1    :", s("", "", 16, 1, 1, 64));
console.log("rfc pass/NaCl N1024 r8:", s("password", "NaCl", 1024, 8, 1, 64));
console.log("pass/NaCl N1024 r1  :", s("password", "NaCl", 1024, 1, 1, 64));
console.log("pass/NaCl N16 r8    :", s("password", "NaCl", 16, 8, 1, 64));
console.log("admin SLZZ          :", s("yunyingsanbu888", "d8d13d74329720a0a69f14ef6222204a", 16384, 8, 1, 64));
