# C# code style

Ensure code is readable and easy to review. Follow these guidelines and consider the reader.
`dotnet format` owns whitespace, usings and line endings; this
file covers layout, naming, comments and expression shape. Rules are **must** unless
marked **prefer**. Apply them to new and changed code only.

## 1. One statement per line

**Must.** Never join statements with `;`.

```csharp
// no
items.Add(item); db.Items.Add(item); await db.SaveChangesAsync(ct);

// yes
items.Add(item);
db.Items.Add(item);
await db.SaveChangesAsync(ct);
```

## 2. Separate the phases

**Must.** Commands read validate → lock/load → decide → write → persist → commit.
Blank line between phases, none inside one just for rhythm.

```csharp
var facts = await Lock(id, ct);
if (facts.User is not { MayLogIn: true }) return;

var request = await db.Requests.SingleOrDefaultAsync(...);
if (request is { InvoiceId: null })
{
    await Initialize(...);
    await tx.CommitAsync(ct);
    return;
}

await Expire(...);
await AllocateCredit(...);

await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

## 3. Name decisions; break long conditions

**Must.** Booleans answer questions (`isActive`, `hasRemainingCoverage`, `canRecover`,
`mayLogIn`). Dates name their boundary (`nextUncoveredStart`, not `start`). Methods
are verb phrases, records nouns. `facts` is accepted for the loaded account
aggregate; avoid `f`, `op`, `d`, `m`. Use domain names in non-trivial lambdas; `x`
is fine only in a one-line projection.

When a condition needs more than one line, name it first. Prefer `is not (A or B)`
over repeated `!=`.

```csharp
var previousLeftGap = previous is { State: Expired or Cancelled }
    && Uncovered(new(previous.StartsOn, previous.EndsOn), covered).Count > 0;
if (previousLeftGap) break;
```

A `continue`/`break` filter also gets a name, so the loop reads as decisions.

## 4. Comments explain why

**Must.** One line at the decision: lock/transaction ownership, idempotency,
held/released money, review instead of throw, inclusive/exclusive boundary. Do not
restate the code or leave commented-out code.

## 5. Give queries a shape

**Must.** One LINQ operator per line once the chain is not a single glance. Name
derived order keys instead of nesting lambdas.

```csharp
var byEarliestCharge = invoices
    .OrderBy(invoice => OpenLines(invoice).Min(line => (DateOnly?)line.ChargeDueOn))
    .ThenBy(invoice => invoice.IssuedAt);

return byEarliestCharge.SelectMany(invoice => OpenLines(invoice)
    .OrderBy(LinePriority)
    .ThenBy(line => line.ChargeDueOn));
```

**Prefer** methods that fit one screen. A method needing a comment every three lines
is usually two named steps.

## 6. Make money and dates explicit

**Must.**

- Validate external money at the boundary: positive, cents only, within the available
  balance. Use `Pricing.Round` for the cents check. Journal amounts are already cents;
  never introduce an unrounded product or division into a movement.
- `clock.Today` for business dates, `clock.Now` for audit timestamps. Never
  `DateTime.Now` or `DateTime.UtcNow`.
- Periods are start-inclusive, end-exclusive: `start <= on && on < end`.
- `DateOnly` for calendar dates, `DateTimeOffset` for timestamps; do not convert to
  `DateTime` for date arithmetic.

```csharp
var isCentAmount = amount > 0 && Pricing.Round(amount) == amount;
if (!isCentAmount || amount > available)
    throw new ValidationException("Use a positive cent amount within the balance.");
```

## 7. Make the failure path explicit

**Must.** Three outcomes, consistently:

| Situation | Outcome |
| --- | --- |
| Invalid caller input | throw `ValidationException`; write nothing |
| External fact cannot be applied | persist with `ReceiptState.Review` and a reason |
| Broken invariant | throw `InvalidOperationException`; roll back |

Never silently drop a bank receipt or overwrite a source fact. Keep the review branch
visibly separate from the success branch.

## 8. Tests follow the same layout

**Must.** One arrange step per line; blank line before the act and the assertions.
The name states behaviour and risk, and it may be a sentence.

```csharp
On(6, 1);
var app = CommandsApp();
var id = await Admit(app, board);

var view = await View(app, id);

Assert.True(view.Membership.Active);
Assert.Equal(10m, view.Remaining);
```

Assert money facts (`Balances`, movements, receipt states), not only a view model.

## Review questions

- Can I follow validation → commit without jumping around?
- Is every joined statement split, and every non-obvious decision named and explained?
- Are review, validation and invariant failures distinct?
- Are money and date boundaries explicit?
- Do tests read as arrange / act / assert and assert money facts?
