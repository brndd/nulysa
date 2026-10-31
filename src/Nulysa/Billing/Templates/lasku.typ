// Adapted from hacklab-document-templates/parametric/lasku/lasku.typ.
// Trusted deployment template; all invoice values arrive as JSON, never evaluated markup.
#import "pankkiviivakoodi.typ": pad-zero, validate-iban, validate-reference, bank-payload, bank-barcode
#let invoice = json(sys.inputs.at("invoice"))
#let sender = invoice.sender
#let recipient = invoice.recipient
#let fi = invoice.language == "fi"
#let label(en, finnish) = if fi { finnish } else { en }
#let parse-date(value) = {
  let parts = value.split("-").map(int)
  datetime(year: parts.at(0), month: parts.at(1), day: parts.at(2))
}
#let date = parse-date(invoice.date)
#let due-date = parse-date(invoice.due_date)
#let iban = validate-iban(sender.iban)
#let reference = validate-reference(invoice.reference)
#let money(cents) = str(calc.quo(cents, 100)) + "," + pad-zero(calc.rem(cents, 100), 2) + " €"
#let lines(values) = values.map(value => [#value]).join(linebreak())
#let field(title, value) = [#text(size: 8pt, fill: luma(90))[#title] \ #value]
#let total = invoice.items.map(item => item.amount_cents).sum(default: 0)
#let payable = invoice.remaining_cents
#assert(payable >= 0 and total - invoice.paid_cents - invoice.held_cents == payable)
#let payload = if payable > 0 and payable <= 99999999 and iban.starts-with("FI") and due-date.year() <= 2099 { bank-payload(iban, reference, payable, due-date) }
#set document(title: [#label("Payment request", "Maksupyyntö") #invoice.number — #sender.name], author: sender.name)
#set text(lang: if fi { "fi" } else { "en" }, size: 10pt)
#set par(justify: false, leading: 0.45em, spacing: 0.6em)
#set page(
  paper: "a4", margin: (top: 16mm, bottom: 18mm, x: 18mm),
  header: context {
    if counter(page).get().first() > 1 [#smallcaps[#sender.name #h(1fr) #invoice.number] #line(length: 100%, stroke: 0.4pt)]
  },
  footer: context align(center, text(size: 8pt)[
    #sender.name · #sender.business_id · #sender.email \
    #label("Payment request", "Maksupyyntö") #invoice.number · #counter(page).display("1/1", both: true)
  ]),
)
// Preserve the original envelope-window layout without its association-specific logo.
#let fitted(width, height, body) = context {
  assert(measure(block(width: width, body)).height <= height, message: "Address details exceed the reserved space")
  block(width: width, height: height, body)
}
#block(width: 100%, height: 94mm, breakable: false)[
  #place(top + left, dx: 107mm)[
    #fitted(67mm, 28mm)[
      #set text(size: 9pt)
      *#sender.name* \ #lines(sender.address) \
      #text(size: 8pt)[#sender.business_id] \ #text(size: 8pt)[#sender.email]
    ]
  ]
  #place(top + left, dx: 5mm, dy: 32mm)[
    #fitted(80mm, 24mm)[*#recipient.name* \ #lines(recipient.address)]
  ]
  #place(top + left, dx: 107mm, dy: 40mm)[
    #fitted(67mm, 50mm)[
      #text(size: 18pt, weight: "bold")[#label("PAYMENT REQUEST", "MAKSUPYYNTÖ")]
      #v(3mm)
      #grid(columns: (auto, 1fr), column-gutter: 4mm, row-gutter: 2mm,
        [#label("Number", "Numero")], [*#invoice.number*],
        [#label("Issued", "Päiväys")], [#date.display("[day].[month].[year]")],
        [#label("Due", "Eräpäivä")], [*#due-date.display("[day].[month].[year]")*],
      )
    ]
  ]
]
#label("Advance payment request. Unpaid requests expire; they are not debt. Period end dates are exclusive.", "Ennakkomaksupyyntö. Maksamaton pyyntö raukeaa eikä muodosta velkaa. Jakson päättymispäivä ei sisälly jaksoon.")
#v(4mm)
#table(columns: (1fr, auto, auto), inset: (x: 5pt, y: 7pt), align: (left, left, right),
  stroke: (x, y) => (bottom: 0.4pt + luma(210)), fill: (x, y) => if y == 0 { luma(245) },
  table.header([*#label("Description / period", "Kuvaus / jakso")*], [*#label("Status", "Tila")*], [*EUR*]),
  ..invoice.items.map(item => (
    [#item.description \ #text(size: 8pt)[#item.period]],
    [#if item.state == "Fulfilled" { label("Completed", "Maksettu") } else { label("Open", "Avoin") }],
    [#money(item.amount_cents)],
  )).flatten(),
)
#block(width: 100%, breakable: false)[
  #v(3mm)
  #align(right)[
    #grid(columns: (auto, auto), column-gutter: 12mm, row-gutter: 2mm, align: (left, right),
      [#label("Total charges", "Maksut yhteensä")], [#money(total)],
      [#label("Completed purchases", "Maksetut ostot")], [#money(invoice.paid_cents)],
      [#label("Funding held for open lines", "Avoimille riveille kohdistettu")], [#money(invoice.held_cents)],
      [*#label("Remaining to pay", "Maksettavaa")*], [*#money(payable)*],
    )
  ]
]
#v(1fr)
#block(width: 100%, breakable: false)[
  #v(5mm) #line(length: 100%, stroke: 0.6pt) #v(3mm)
  #grid(columns: (1fr, 1fr), column-gutter: 8mm, row-gutter: 3mm,
    field(label("Payee", "Maksun saaja"), sender.name), field(label("Reference", "Viitenumero"), reference),
    field("IBAN", sender.iban), field(label("Due", "Eräpäivä"), due-date.display("[day].[month].[year]")),
    field("BIC / Bank", [#sender.bic / #sender.bank]), field(label("Payable", "Maksettava"), strong(money(payable))),
  )
  #if payload != none [#v(8mm) #align(center)[#bank-barcode(payload)]]
]
