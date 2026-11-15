package com.musicplayer.android.playback

/**
 * Equal-power crossfade gain with the user's output volume applied as the
 * base. Kept pure (no Android imports) so the volume/crossfade contract can
 * be unit-tested on the JVM.
 */
internal fun crossfadeGain(baseVolume: Float, curve: Float): Float =
    (baseVolume.coerceIn(0f, 1f) * curve.coerceIn(0f, 1f)).coerceIn(0f, 1f)
