# Swagger UI assets

Vendored from `swagger-ui-dist` **5.34.1** (Swagger API / SmartBear), Apache-2.0.
The upstream LICENSE and NOTICE are included in this directory and embedded with the UI.

Source: https://registry.npmjs.org/swagger-ui-dist/-/swagger-ui-dist-5.34.1.tgz

Verified npm SHA-512 integrity:
`sha512-VSr8fXfI93obYaQicLJbLUKGRxDojj/SfRJDDLCioGTUBj4BK/Nj4eE9jCeCAnJxVLi7iS+08PbiZLyrQKBkJQ==`

`swagger-ui-bundle.js` and `swagger-ui.css` are unmodified distribution files.
`index.html` is the application's local entry page. It loads all assets from Hive,
disables the remote specification validator and does not persist API credentials.
To upgrade, verify the pinned npm archive's integrity, replace the two assets plus
LICENSE/NOTICE, update this record, and run the Swagger integration and AOT smoke tests.
