export interface AuthTokens {
  accessToken: string
  accessTokenExpiresAt: string
  refreshToken: string
  refreshTokenExpiresAt: string
  role: string
}

export interface LoginResponse {
  twoFactorRequired?: boolean
  challengeToken?: string
  accessToken?: string
  accessTokenExpiresAt?: string
  refreshToken?: string
  refreshTokenExpiresAt?: string
  role?: string
}

export interface WidgetView {
  id: string
  sku: string
  name: string
  description: string
  imageUrl: string | null
  price: number
  isActive: boolean
  quantityOnHand: number
  quantityReserved: number
  quantityAvailable: number
}

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface CartLine {
  widgetId: string
  sku: string
  name: string
  unitPrice: number
  quantity: number
  quantityAvailable: number
  lineSubtotal: number
}

export interface CartView {
  id: string
  userId: string | null
  items: CartLine[]
  subtotal: number
  itemCount: number
}

export interface OrderQuote {
  subtotal: number
  shippingMethod: string
  shipping: number
  stateCode: string
  taxRate: number
  tax: number
  total: number
  itemCount: number
  isEmpty: boolean
}

export interface CheckoutResult {
  orderNumber: string
  orderId: string
  status: string
  total: number
  paymentProvider: string
  paymentReference: string
}

export interface OrderItemView {
  widgetId: string
  sku: string
  name: string
  unitPrice: number
  quantity: number
  lineSubtotal: number
}

export interface OrderView {
  id: string
  orderNumber: string
  status: string
  email: string
  subtotal: number
  shippingMethod: string
  shipping: number
  taxState: string
  taxRate: number
  tax: number
  total: number
  paymentProvider: string | null
  paymentReference: string | null
  trackingNumber: string | null
  createdAt: string
  /** Set while the charge outcome is unknown: the order holds stock and awaits reconciliation. */
  paymentUnconfirmedAt: string | null
  /** Cumulative amount refunded. A part-refunded order is still Paid, with goods owed. */
  refundedTotal: number
  items: OrderItemView[]
}

export interface OrderSummary {
  id: string
  orderNumber: string
  status: string
  total: number
  itemCount: number
  createdAt: string
  email: string | null
  paymentUnconfirmedAt: string | null
}

/** Everything on the payment path that needs a person. Empty on a healthy day. */
export interface PaymentExceptions {
  /** Charges the provider never confirmed. These orders hold stock while nobody knows. */
  unconfirmed: OrderSummary[]
  /** Orders that look like the same purchase twice: a heuristic, for a human to judge. */
  possibleDuplicates: OrderSummary[]
}

/** What the account area knows about the person signed in. Email is read-only; only the name edits. */
export interface ProfileView {
  email: string
  displayName: string | null
  role: string
  twoFactorEnabled: boolean
  /** False for a Google-only account, which has no password to change. */
  hasPassword: boolean
  memberSince: string
}

/** The token pair returned when a password change replaces the current session. */
export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: string
  refreshToken: string
  refreshTokenExpiresAt: string
  role: string
}
