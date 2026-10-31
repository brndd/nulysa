// Adapted from hacklab-document-templates/parametric/lasku/pankkiviivakoodi.typ.
// Finance Finland Bank Bar Code Guide v5.3. No fonts or remote packages required.
#let compact(value) = upper(value.replace(regex("\\s"), ""))
#let digits(value) = value.match(regex("^[0-9]+$")) != none
#let pad-zero(value, width) = {
  let value = str(value)
  assert(value.len() <= width, message: "Barcode field exceeds its maximum length")
  "0" * (width - value.len()) + value
}
#let mod97(value) = {
  let remainder = 0
  for char in value {
    let encoded = if digits(char) { char } else { str(str.to-unicode(char) - 55) }
    for digit in encoded { remainder = calc.rem(remainder * 10 + int(digit), 97) }
  }
  remainder
}
#let validate-iban(value) = {
  let value = compact(value)
  assert(value.match(regex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$")) != none, message: "Invalid IBAN format")
  assert(mod97(value.slice(4) + value.slice(0, 4)) == 1, message: "IBAN checksum does not match")
  value
}
#let domestic-check-digit(base) = {
  let sum = 0
  for (i, digit) in base.clusters().rev().enumerate() { sum += int(digit) * (7, 3, 1).at(calc.rem(i, 3)) }
  str(calc.rem(10 - calc.rem(sum, 10), 10))
}
#let validate-reference(value) = {
  let value = compact(value)
  if value.starts-with("RF") {
    assert(value.match(regex("^RF[0-9]{2}[A-Z0-9]{1,21}$")) != none, message: "Invalid RF reference format")
    assert(mod97(value.slice(4) + value.slice(0, 4)) == 1, message: "RF reference checksum does not match")
  } else {
    assert(digits(value) and value.len() >= 4 and value.len() <= 20 and not value.starts-with("0"), message: "Domestic reference must contain 4–20 digits")
    assert(domestic-check-digit(value.slice(0, -1)) == value.at(-1), message: "Reference number checksum does not match")
  }
  value
}
#let bank-payload(iban, reference, cents, due-date) = {
  let iban = validate-iban(iban)
  let reference = validate-reference(reference)
  assert(iban.starts-with("FI") and iban.len() == 18 and digits(iban.slice(2)))
  assert(cents >= 0 and cents <= 99999999)
  assert(due-date.year() >= 2000 and due-date.year() <= 2099)
  let rf = reference.starts-with("RF")
  let ref-field = if rf {
    assert(digits(reference.slice(2)))
    reference.slice(2, 4) + pad-zero(reference.slice(4), 21)
  } else { "000" + pad-zero(reference, 20) }
  (if rf { "5" } else { "4" }) + iban.slice(2) + pad-zero(cents, 8) + ref-field + due-date.display("[year repr:last_two][month][day]")
}
#let patterns = (
  "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
  "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
  "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
  "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
  "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
  "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
  "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
  "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
  "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
  "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
  "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
)
#let code128-symbols(payload) = {
  assert(payload.len() == 54 and digits(payload))
  let pairs = range(0, payload.len(), step: 2).map(i => int(payload.slice(i, i + 2)))
  let checksum = 105
  for (i, pair) in pairs.enumerate() { checksum += (i + 1) * pair }
  (105,) + pairs + (calc.rem(checksum, 103), 106)
}
#let bank-barcode(payload) = {
  let runs = code128-symbols(payload).map(symbol => patterns.at(symbol)).join("")
  let module-width = 105mm / 332
  box(width: 145mm, height: 12mm, {
    let x = 20mm
    for (i, run) in runs.clusters().enumerate() {
      let width = int(run) * module-width
      if calc.rem(i, 2) == 0 { place(top + left, dx: x, rect(width: width, height: 12mm, fill: black, stroke: none)) }
      x += width
    }
  })
}
