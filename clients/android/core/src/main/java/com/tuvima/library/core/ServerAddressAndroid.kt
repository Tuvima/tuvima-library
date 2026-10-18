package com.tuvima.library.core

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Remembers the outside address in the app's private preferences, keyed by the home address it belongs to, so pairing with a
 * different server never inherits another server's outside address. It holds no secret: the sign-in tokens stay in
 * [SecureTokenStore].
 */
class PreferencesServerAddressStore(context: Context) : ServerAddressStore {
    private val preferences = context.getSharedPreferences("tuvima_server_addresses", Context.MODE_PRIVATE)

    override fun loadOutside(home: String): String? = preferences.getString(key(home), null)

    override fun saveOutside(home: String, outside: String?) {
        preferences.edit().apply {
            if (outside == null) remove(key(home)) else putString(key(home), outside)
        }.apply()
    }

    private fun key(home: String) = "outside:$home"
}

/**
 * Runs [recheck] shortly after the phone's default network changes (Wi-Fi to mobile data, another Wi-Fi, back online), so the
 * app moves to the home or outside address on its own. Needs only the network-state permission: no Wi-Fi name, no location.
 * Changes arrive in bursts, so they are collapsed into one check [SETTLE_MS] after the last one.
 */
class NetworkChangeWatcher(
    context: Context,
    private val scope: CoroutineScope,
    private val recheck: suspend () -> Unit,
) {
    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private var pending: Job? = null

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = schedule()

        override fun onLost(network: Network) = schedule()
    }

    fun start() {
        connectivity.registerDefaultNetworkCallback(callback)
    }

    fun stop() {
        connectivity.unregisterNetworkCallback(callback)
        pending?.cancel()
    }

    @Synchronized
    private fun schedule() {
        pending?.cancel()
        pending = scope.launch {
            delay(SETTLE_MS)
            try {
                recheck()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // A failed check leaves the address as it was; the next network change or request failure checks again.
                Log.w(TAG, "Checking the server address after a network change failed", failure)
            }
        }
    }

    private companion object {
        const val SETTLE_MS = 800L
        const val TAG = "TuvimaNetwork"
    }
}
