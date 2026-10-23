-- Simpler access (spec D2, D3, D4): a person sees every library in the areas they have unless an administrator hid it from them, so the
-- table that listed the libraries an account was given is gone and a table that lists the libraries hidden from an account takes its place.
-- One row per library hidden from that account. A Vault library is never listed (the repository refuses it). A household member also
-- follows the hidden libraries of the account it inherits from. Pre-release, so nothing is carried across: after this step everyone sees
-- every library in the areas they have (D3).
DROP TABLE account_library_grants;

CREATE TABLE account_library_hidden (
    account_id            BLOB NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    library_id            BLOB NOT NULL,
    created_at            TEXT NOT NULL,
    created_by_account_id BLOB REFERENCES accounts(id) ON DELETE SET NULL,
    PRIMARY KEY (account_id, library_id)
);
