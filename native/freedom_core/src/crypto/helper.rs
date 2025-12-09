use chacha20poly1305::{ aead::{ Aead, KeyInit }, ChaCha20Poly1305, Nonce };
use hkdf::Hkdf;
use sha2::Sha256;
use x25519_dalek::{ PublicKey, StaticSecret };
use rand::{ RngCore };
use rand::rngs::OsRng;

const NONCE_SIZE: usize = 12;
const KEY_SIZE: usize = 32;

#[derive(Debug, thiserror::Error)]
pub enum CryptoError {
    #[error("Encryption failed")]
    EncryptionError,
    #[error("Decryption failed")]
    DecryptionError,
    #[error("Invalid data length")]
    InvalidLength,
}

/// Derives a session key exactly as NSec's KeyAgreementAlgorithm.X25519 + HkdfSha256 does.
/// C# Reference: _ecdh.Agree(...) -> _kdf.DeriveKey(sharedSecret, null, null, _cipher)
pub fn create_session_key(my_private_key: &StaticSecret, other_public_key: &PublicKey) -> [u8; 32] {
    // 1. ECDH: Calculate raw shared secret
    let shared_secret = my_private_key.diffie_hellman(other_public_key);

    // 2. HKDF-SHA256: Derive the final 32-byte key
    // NSec users "null" for salt, which in RFC 5869 means a string of zeros equal to hash len.
    let hk = Hkdf::<Sha256>::new(None, shared_secret.as_bytes());

    let mut okm = [0u8; KEY_SIZE];

    hk.expand(&[], &mut okm).expect("32 bytes is a valid length for SHA-256 HKDF");

    okm
}

/// Encrypts data matching C# format: [Nonce (12)] + [Ciphertext]
/// C# Reference: EncryptLayer method
pub fn encrypt_layer(session_key: &[u8; 32], plaintext: &[u8]) -> Result<Vec<u8>, CryptoError> {
    let cipher = ChaCha20Poly1305::new(session_key.into());

    // Generate random Nonce (12 bytes)
    let mut nonce_bytes = [0u8; NONCE_SIZE];
    let _ = OsRng.try_fill_bytes(&mut nonce_bytes);
    let nonce = Nonce::from_slice(&nonce_bytes);

    // Encrypt
    // Note: NSec's "default" associated data is empty, which matches here.
    let ciphertext = cipher.encrypt(nonce, plaintext).map_err(|_| CryptoError::EncryptionError)?;

    // Combine: [Nonce] + [Ciphertext]
    let mut output = Vec::with_capacity(NONCE_SIZE + ciphertext.len());
    output.extend_from_slice(&nonce_bytes);
    output.extend_from_slice(&ciphertext);

    Ok(output)
}

/// Decrypts data matching C# format: Input is [Nonce (12)] + [Ciphertext]
/// C# Reference: TryDecryptLayer method
pub fn try_decrypt_layer(
    session_key: &[u8; 32],
    encrypted_packet: &[u8]
) -> Result<Vec<u8>, CryptoError> {
    if encrypted_packet.len() < NONCE_SIZE {
        return Err(CryptoError::InvalidLength);
    }

    let cipher = ChaCha20Poly1305::new(session_key.into());

    // Extract Nonce
    let nonce = Nonce::from_slice(&encrypted_packet[0..NONCE_SIZE]);

    // Extract Ciphertext
    let ciphertext = &encrypted_packet[NONCE_SIZE..];

    // Decrypt
    cipher.decrypt(nonce, ciphertext).map_err(|_| CryptoError::DecryptionError)
}
