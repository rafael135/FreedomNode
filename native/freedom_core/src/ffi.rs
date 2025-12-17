use crate::crypto::helper;
use crate::crypto::handshake::HandshakePayload;
use std::os::linux::raw;
use std::slice;
use std::ptr;


/// Helper to convert raw pointer and length to a byte slice.
unsafe fn raw_to_slice<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        slice::from_raw_parts(ptr, len)
    }
}

// Helper to write data to C# allocated buffer.
unsafe fn write_to_buffer(ptr: *mut u8, len: usize, data: &[u8]) -> i32 {
    if ptr.is_null() || len < data.len() {
        return -1;
    }

    let output = slice::from_raw_parts_mut(ptr, len);
    output[..data.len()].copy_from_slice(data);
    data.len() as i32
}

// --- C# Exports ----


/// Creates a session key using X25519 key exchange.
/// # Safety
/// - `my_private_key_ptr` must point to a valid 32-byte array.
/// - `other_public_key_ptr` must point to a valid 32-byte array.
/// - `output_ptr` must point to a valid 32-byte buffer to write the session key.
/// Returns 1 on success, -1 on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_create_session_key(
    my_private_key_ptr: *const u8, // 32 bytes
    other_public_key_ptr: *const u8, // 32 bytes
    output_ptr: *mut u8, // 32 bytes Buffer to write the session key
) -> i32 {
    let my_private_bytes = raw_to_slice(my_private_key_ptr, 32);
    let other_public_bytes = raw_to_slice(other_public_key_ptr, 32);

    let my_secret = match x25519_dalek::StaticSecret::from(<[u8; 32]>::try_from(my_private_bytes).unwrap()) {
        secret => secret,
    };
    let other_public = match x25519_dalek::PublicKey::from(<[u8; 32]>::try_from(other_public_bytes).unwrap()) {
        public => public,
    };

    let session_key = helper::create_session_key(&my_secret, &other_public);

    if output_ptr.is_null() { return -1; }
    let output_slice = slice::from_raw_parts_mut(output_ptr, 32);
    output_slice.copy_from_slice(&session_key);

    1 // Success
}


/// Validates a handshake payload. (Ed25519 Signature verification)
/// # Safety
/// - `data_ptr` must point to a valid byte array of length `len`.
/// Returns 1 if valid, -1 if invalid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_validate_handshake(
    data_ptr: *const u8,
    len: usize,
) -> i32 {
    let data = raw_to_slice(data_ptr, len);

    match HandshakePayload::from_bytes(data) {
        Ok(payload) => {
            match payload.verify() {
                Ok(_) => 1, // Valid
                Err(_) => -1, // Invalid
            }
        },
        Err(_) => -1, // Invalid
    }
}


/// Encrypts data using ChaCha20-Poly1305.
/// # Safety
/// - `key_ptr` must point to a valid 32-byte array.
/// - `plaintext_ptr` must point to a valid byte array of length `plaintext_len`.
/// - `output_ptr` must point to a valid buffer with capacity `output_cap`.
/// Returns the number of bytes written to `output_ptr`, or -1 on error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ffi_encrypt_layer(
    key_ptr: *const u8, // 32 bytes
    plaintext_ptr: *const u8, // Original data
    plaintext_len: usize,
    output_ptr: *mut u8, // Buffer to write encrypted data
    output_cap: usize, // Capacity of output buffer
) -> i32 {
    let key_bytes = unsafe { raw_to_slice(key_ptr, 32) };
    let plaintext = unsafe { raw_to_slice(plaintext_ptr, plaintext_len) };

    let key_array: [u8; 32] = key_bytes.try_into().unwrap_or([0; 32]);

    match helper::encrypt_layer(&key_array, plaintext) {
        Ok(encrypted_data) => {
            unsafe { write_to_buffer(output_ptr, output_cap, &encrypted_data) }
        },
        Err(_) => -1,
    }
}