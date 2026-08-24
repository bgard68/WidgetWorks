import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfirmDialog } from './ConfirmDialog'

/**
 * The native-dialog wrapper every destructive action goes through. The busy
 * state is the part worth guarding: while a delete is in flight nothing may
 * close the dialog or fire a second request.
 */
describe('ConfirmDialog', () => {
  const noop = () => {}

  it('opens as a modal with default button labels', () => {
    render(
      <ConfirmDialog open title="Sure?" onConfirm={noop} onCancel={noop}>
        <p>Body</p>
      </ConfirmDialog>,
    )

    expect(screen.getByRole('dialog')).toHaveAttribute('open')
    expect(screen.getByRole('button', { name: 'Confirm' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument()
  })

  it('stays closed until told to open, then closes again', () => {
    const { rerender, container } = render(
      <ConfirmDialog open={false} title="Sure?" onConfirm={noop} onCancel={noop}>x</ConfirmDialog>,
    )
    const dialog = container.querySelector('dialog') as HTMLDialogElement
    expect(dialog.open).toBe(false)

    rerender(<ConfirmDialog open title="Sure?" onConfirm={noop} onCancel={noop}>x</ConfirmDialog>)
    expect(dialog.open).toBe(true)

    rerender(<ConfirmDialog open={false} title="Sure?" onConfirm={noop} onCancel={noop}>x</ConfirmDialog>)
    expect(dialog.open).toBe(false)
  })

  it('confirms and cancels through the buttons', async () => {
    const onConfirm = vi.fn()
    const onCancel = vi.fn()
    const user = userEvent.setup()
    render(
      <ConfirmDialog open title="Sure?" confirmLabel="Delete" cancelLabel="Keep" danger onConfirm={onConfirm} onCancel={onCancel}>
        x
      </ConfirmDialog>,
    )

    await user.click(screen.getByRole('button', { name: 'Delete' }))
    await user.click(screen.getByRole('button', { name: 'Keep' }))

    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onCancel).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('button', { name: 'Delete' })).toHaveClass('btn-danger-solid')
  })

  it('treats Escape as cancel', () => {
    const onCancel = vi.fn()
    render(<ConfirmDialog open title="Sure?" onConfirm={noop} onCancel={onCancel}>x</ConfirmDialog>)

    // The native dialog turns Escape into a 'cancel' event.
    fireEvent(screen.getByRole('dialog'), new Event('cancel', { bubbles: true, cancelable: true }))

    expect(onCancel).toHaveBeenCalledTimes(1)
  })

  it('while busy: buttons disable, the label says so, and Escape is ignored', () => {
    const onCancel = vi.fn()
    render(
      <ConfirmDialog open busy title="Sure?" confirmLabel="Delete" onConfirm={noop} onCancel={onCancel}>
        x
      </ConfirmDialog>,
    )

    expect(screen.getByRole('button', { name: 'Working…' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled()

    fireEvent(screen.getByRole('dialog'), new Event('cancel', { bubbles: true, cancelable: true }))
    expect(onCancel).not.toHaveBeenCalled()
  })
})
