package com.musicplayer.android.data.api

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Refresh-token persistence interface (tests use an in-memory implementation). */
interface RefreshTokenStore {
    fun load(): String
    fun save(token: String): Boolean
    fun clear()
}

/**
 * Refresh token encrypted with an Android Keystore AES/GCM key. Only the
 * ciphertext + IV lands in SharedPreferences; the key never leaves the
 * Keystore. A Keystore failure degrades to an in-memory session instead of
 * writing a plaintext token.
 */
class SecureTokenStore(context: Context) : RefreshTokenStore {
    private val prefs = context.applicationContext
        .getSharedPreferences("hathor_auth", Context.MODE_PRIVATE)

    override fun load(): String {
        val stored = prefs.getString(KEY_REFRESH, null) ?: return ""
        return try {
            val (ivPart, dataPart) = stored.split(':', limit = 2)
            val iv = Base64.decode(ivPart, Base64.NO_WRAP)
            val data = Base64.decode(dataPart, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, secretKey() ?: return "", GCMParameterSpec(128, iv))
            String(cipher.doFinal(data), Charsets.UTF_8)
        } catch (e: Exception) {
            Log.w(TAG, "Stored refresh token could not be decrypted", e)
            clear()
            ""
        }
    }

    override fun save(token: String): Boolean {
        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, secretKey() ?: return false)
            val encrypted = cipher.doFinal(token.toByteArray(Charsets.UTF_8))
            val payload = Base64.encodeToString(cipher.iv, Base64.NO_WRAP) + ":" +
                Base64.encodeToString(encrypted, Base64.NO_WRAP)
            prefs.edit().putString(KEY_REFRESH, payload).apply()
            true
        } catch (e: Exception) {
            Log.w(TAG, "Could not store refresh token securely", e)
            false
        }
    }

    override fun clear() {
        prefs.edit().remove(KEY_REFRESH).apply()
    }

    private fun secretKey(): SecretKey? {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val TAG = "SecureTokenStore"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "hathor_refresh_token"
        const val KEY_REFRESH = "refresh_token"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
    }
}

class MemoryRefreshTokenStore(
    private var token: String = "",
) : RefreshTokenStore {
    override fun load(): String = token
    override fun save(token: String): Boolean {
        this.token = token
        return true
    }
    override fun clear() {
        token = ""
    }
}
