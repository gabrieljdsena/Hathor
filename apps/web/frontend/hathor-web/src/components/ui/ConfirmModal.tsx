import Modal from './Modal'
import { GhostButton, PrimaryButton } from './fields'

// Shared confirm dialog (download already-owned guard, playlist/podcast
// deletes): glass Modal shell + Ghost cancel + Primary confirm.
export default function ConfirmModal({
  open,
  title,
  message,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean
  title: string
  message: string
  confirmLabel?: string
  cancelLabel?: string
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <Modal open={open} onClose={onCancel} title={title}>
      <p className="text-sm text-zinc-300 leading-relaxed">{message}</p>
      <div className="mt-5 flex justify-end gap-2">
        <GhostButton onClick={onCancel} disabled={busy}>
          {cancelLabel}
        </GhostButton>
        <PrimaryButton onClick={onConfirm} loading={busy}>
          {confirmLabel}
        </PrimaryButton>
      </div>
    </Modal>
  )
}
