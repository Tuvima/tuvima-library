# Webhook channel (sample)

A notification channel written as JSON alone. Copy this folder into the Library's plugins folder, change the address and the topic name, switch the plugin on from Settings > Plugins, enter the access token on its card, and choose it for an event under Settings > Notifications. Use "Send test" there to check it.

Only data leaves the Library: the notification's title and message, to the address listed under `network`. The login stays in the secrets store and is added to the request when it is sent.
