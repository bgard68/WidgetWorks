import { Link, useNavigate } from 'react-router-dom'
import { useCart } from '../cart/CartContext'
import { money } from '../lib/format'

export function CartPage() {
  const { cart, updateItem, removeItem } = useCart()
  const navigate = useNavigate()

  if (!cart || cart.items.length === 0) {
    return (
      <section>
        <h1>Your cart</h1>
        <p className="muted">Your cart is empty. <Link to="/">Browse widgets →</Link></p>
      </section>
    )
  }

  return (
    <section>
      <h1>Your cart</h1>
      <table className="table">
        <thead>
          <tr><th>Widget</th><th>Price</th><th>Qty</th><th>Subtotal</th><th></th></tr>
        </thead>
        <tbody>
          {cart.items.map((line) => (
            <tr key={line.widgetId}>
              <td>{line.name}</td>
              <td>{money(line.unitPrice)}</td>
              <td>
                <input
                  type="number"
                  min={0}
                  max={line.quantityAvailable}
                  value={line.quantity}
                  onChange={(e) => updateItem(line.widgetId, Math.max(0, Number(e.target.value)))}
                />
              </td>
              <td>{money(line.lineSubtotal)}</td>
              <td><button className="linkbtn" onClick={() => removeItem(line.widgetId)}>Remove</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="cart-total">Subtotal: <strong>{money(cart.subtotal)}</strong></div>
      <button onClick={() => navigate('/checkout')}>Proceed to checkout</button>
    </section>
  )
}
