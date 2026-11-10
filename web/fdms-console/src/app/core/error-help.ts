export interface ErrorHelp { what: string; todo: string; /** correct = the correct dialog fixes it; reissue = send a corrected invoice; none = nothing to click */ fix?: 'correct' | 'reissue' }

/** ZIMRA and gateway codes in plain words, with the one thing to do. Anything not listed shows its code to look up in the FDMS error list. */
export const ERROR_HELP: Record<string, ErrorHelp> = {
  RCPT016: { what: 'The invoice has no items.', todo: 'Add at least one item and send it again.' },
  RCPT023: { what: 'An item has a quantity of zero or less.', todo: 'Set the quantity above zero.' },
  RCPT025: { what: 'An item uses a tax rate that is not set up on this device.', todo: 'Refresh the taxes from ZIMRA on the invoice screen and pick a rate from the list.' },
  RCPT029: { what: 'A normal invoice was sent with the number of another invoice attached.', todo: 'Only credit and debit notes point at an original invoice.' },
  RCPT032: { what: 'The invoice a credit or debit note refers to was not found, or ZIMRA has not accepted it yet.', todo: 'Wait until the original shows Submitted, then send the note.' },
  RCPT033: { what: 'The original invoice is more than 12 months old.', todo: 'ZIMRA does not allow credit or debit notes against it any more.' },
  RCPT035: { what: 'The credit note is bigger than what is left on the original invoice.', todo: 'Reduce the credit to what remains.' },
  RCPT036: { what: 'The note uses a tax that was not on the original invoice.', todo: 'Use only taxes that the original invoice had.' },
  RCPT039: { what: 'The payments do not add up to the invoice total.', todo: 'Make the payment equal to the total (the gateway calculates it when you do not give one).' },
  RCPT040: { what: 'The invoice total is negative.', todo: 'Check the discounts and prices.' },
  RCPT043: { what: 'A credit or debit note must use the same currency as the original invoice.', todo: 'Change the currency to match.' },
  RCPT047: { what: 'An item has no HS code. VAT-registered businesses must give one on every item.', todo: 'Add an 8-digit HS code to each item (or choose the item from your catalogue).', fix: 'correct' },
  RCPT048: { what: 'An item has an HS code of the wrong length. Tariff codes have 4, 6 or 8 digits (ZIMRA uses 8).', todo: 'Correct the HS code. The correct dialog changes it with a credit note and a corrected invoice.', fix: 'correct' },
  FISC04: { what: 'There is no open fiscal day to close.', todo: 'Nothing to do. The next receipt opens a new day.' },
  RECEIPTS_PENDING: { what: 'Some receipts have not reached ZIMRA yet, so the day cannot close.', todo: 'Open Device health and press “Send them now”, then close the day.' },
  FDMS_UNAVAILABLE: { what: 'ZIMRA could not be reached.', todo: 'Check the internet connection. Receipts are kept and sent automatically when it is back.' },
  PAYMENT_REQUIRED: { what: 'Your account is waiting for a payment.', todo: 'Pay your invoice under Billing and everything opens again.' },
  PLAN_LIMIT: { what: 'Your plan does not allow any more of this.', todo: 'Upgrade your plan under Billing.' },
};

export function explain(code?: string | null): ErrorHelp | null { return code ? ERROR_HELP[code] ?? null : null; }
