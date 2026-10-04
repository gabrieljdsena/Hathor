import Icon, { type IconName } from './icons'

// The three button shapes used across desktop ui/:
// ghost control (player bar toggles), orange circle (play-all), big white play/pause.
interface ControlButtonProps {
  icon: IconName
  title: string
  active?: boolean
  loading?: boolean
  disabled?: boolean
  onClick?: () => void
  iconClass?: string
}

export function ControlButton({
  icon,
  title,
  active = false,
  loading = false,
  disabled = false,
  onClick,
  iconClass = 'w-6 h-6',
}: ControlButtonProps) {
  return (
    <button
      onClick={onClick}
      title={title}
      disabled={disabled || loading}
      aria-busy={loading}
      className={`p-2 rounded-full transition-all duration-300 hover:scale-110 active:scale-95 hover:bg-white/5 cursor-pointer focus:outline-none disabled:opacity-50 disabled:hover:scale-100 disabled:cursor-wait ${
        active ? 'text-orange-500' : 'text-zinc-400 hover:text-white'
      }`}
    >
      {loading ? <span className="spinner-btn" aria-hidden="true" /> : <Icon name={icon} className={iconClass} />}
    </button>
  )
}

export function PlayCircleButton({
  onClick,
  title = 'Play',
  loading = false,
  disabled = false,
}: {
  onClick?: () => void
  title?: string
  loading?: boolean
  disabled?: boolean
}) {
  return (
    <button
      onClick={onClick}
      title={title}
      disabled={disabled || loading}
      aria-busy={loading}
      className="flex items-center justify-center w-11 h-11 bg-orange-500 text-white rounded-full hover:bg-orange-400 transition-all duration-300 hover:scale-105 active:scale-95 shadow-[0_0_20px_rgba(249,115,22,0.3)] focus:outline-none cursor-pointer disabled:opacity-60 disabled:hover:scale-100 disabled:cursor-wait"
    >
      {loading ? (
        <span className="spinner-btn" aria-hidden="true" />
      ) : (
        <Icon name="play" className="w-5 h-5 ml-0.5" />
      )}
    </button>
  )
}

export function PlayPauseButton({
  playing,
  onClick,
  id = 'playPauseBtn',
  small = false,
}: {
  playing: boolean
  onClick?: () => void
  id?: string
  small?: boolean
}) {
  return (
    <button
      id={id}
      onClick={onClick}
      className={`flex items-center justify-center bg-white text-zinc-950 hover:bg-orange-400 hover:text-white transition-all duration-300 hover:scale-105 active:scale-95 rounded-full shadow-[0_0_15px_rgba(255,255,255,0.1)] cursor-pointer focus:outline-none ${
        small ? 'w-10 h-10' : 'w-14 h-14'
      }`}
    >
      <Icon
        name={playing ? 'pause' : 'play'}
        className={small ? (playing ? 'w-6 h-6' : 'w-6 h-6 ml-0.5') : (playing ? 'w-8 h-8' : 'w-8 h-8 ml-1')}
      />
    </button>
  )
}
