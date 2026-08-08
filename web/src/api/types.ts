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
  items: OrderItemView[]
}

export interface OrderSummary {
  id: string
  orderNumber: string
  status: string
  total: number
  itemCount: number
  createdAt: string
}
