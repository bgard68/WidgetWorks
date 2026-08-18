// Order status rendered as a coloured pill, so a status is scannable in a list
// instead of reading as plain text.
const TONE: Record<string, string> = {
  Paid: 'pill-ok',
  Shipped: 'pill-info',
  Delivered: 'pill-ok',
  AwaitingPayment: 'pill-warn',
  Pending: 'pill-warn',
  PaymentFailed: 'pill-err',
  Cancelled: 'pill-err',
}

/** "AwaitingPayment" -> "Awaiting payment" */
function humanise(status: string): string {
  const spaced = status.replace(/([a-z])([A-Z])/g, '$1 $2')
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase()
}

export function StatusPill({ status }: { status: string }) {
  return <span className={`pill ${TONE[status] ?? 'pill-info'}`}>{humanise(status)}</span>
}
