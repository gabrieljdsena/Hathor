import Icon from './icons'

// Gradient brand tile + wordmark (sidebar header, login card).
export default function Brand({
  boxClass = 'w-8 h-8 rounded-lg',
  iconClass = 'w-5 h-5 text-white',
  wordmark = true,
  wordmarkClass = 'text-lg font-bold text-transparent bg-clip-text bg-gradient-to-r from-zinc-100 to-zinc-400 tracking-wide',
}: {
  boxClass?: string
  iconClass?: string
  wordmark?: boolean
  wordmarkClass?: string
}) {
  return (
    <>
      <div
        className={`${boxClass} bg-gradient-to-br from-orange-400 to-orange-600 flex items-center justify-center flex-shrink-0 shadow-lg shadow-orange-500/30`}
      >
        <Icon name="brand" className={iconClass} />
      </div>
      {wordmark && <span className={wordmarkClass}>Hathor</span>}
    </>
  )
}
