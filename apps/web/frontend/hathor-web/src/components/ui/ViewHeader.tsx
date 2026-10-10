import type { ReactNode } from 'react'
import Icon, { type IconName } from './icons'

// Section header: gradient HeaderIconTile + title + subtitle + right-side actions.
// Full (Home, text-3xl) and compact (list views, text-2xl) variants.
export default function ViewHeader({
  icon,
  title,
  subtitle,
  actions,
  compact = false,
}: {
  icon: IconName
  title: string
  subtitle?: ReactNode
  actions?: ReactNode
  compact?: boolean
}) {
  return (
    <div className="flex items-center gap-4 max-[700px]:flex-wrap max-[700px]:gap-3">
      <div
        className={`inline-flex items-center justify-center rounded-xl bg-orange-500/10 border border-orange-500/20 text-orange-400 shadow-[0_0_15px_rgba(249,115,22,0.15)] ${
          compact ? 'w-10 h-10' : 'w-12 h-12'
        }`}
      >
        <Icon name={icon} className={compact ? 'w-5 h-5' : 'w-6 h-6'} />
      </div>
      <div className="min-w-0">
        <h1 className={`font-bold text-zinc-100 truncate ${compact ? 'text-2xl' : 'text-3xl'}`}>{title}</h1>
        {subtitle !== undefined && (
          <p className="text-sm text-zinc-500 mt-1 tracking-wide">{subtitle}</p>
        )}
      </div>
      {actions && (
        <div className="ml-auto flex items-center gap-2 max-[700px]:ml-0 max-[700px]:w-full max-[700px]:justify-start max-[700px]:flex-wrap">
          {actions}
        </div>
      )}
    </div>
  )
}
